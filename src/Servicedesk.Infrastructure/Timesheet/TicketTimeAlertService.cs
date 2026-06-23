using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Timesheet;

/// v0.0.87 — backend for the per-ticket hour-limit alert. Owns the limit
/// arithmetic (global default, per-queue override, per-ticket grants), the
/// over-limit decision, and the two timeline events the agent's choices
/// produce.
///
/// Resolution is queue-aware: the queue active on the ticket decides whether
/// the alert is on at all ('inherit' the global switch / force 'on' / force
/// 'off') and which limit applies (its own threshold override, else the global
/// default). Per-ticket grants always add on top of the resolved limit.
///
/// All timing/threshold logic lives here, never on the client: the client only
/// renders the snapshot this service returns.
public sealed class TicketTimeAlertService : ITicketTimeAlertService
{
    // Sanity cap on a single grant so a typo can't push the limit to absurd
    // values. 100_000 min ≈ 1666 h — far above any real ticket.
    private const int MaxExtendMinutes = 100_000;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly ITicketRepository _tickets;
    private readonly ILogger<TicketTimeAlertService> _logger;

    public TicketTimeAlertService(
        NpgsqlDataSource dataSource, ISettingsService settings,
        ITicketRepository tickets, ILogger<TicketTimeAlertService> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _tickets = tickets;
        _logger = logger;
    }

    public async Task<TicketTimeAlertStatus> GetStatusAsync(Guid ticketId, CancellationToken ct = default)
    {
        var globalEnabled = await _settings.GetAsync<bool>(SettingKeys.Timesheet.TimeAlertEnabled, ct);
        var globalThreshold = await _settings.GetAsync<int>(SettingKeys.Timesheet.TimeAlertThresholdMinutes, ct);
        var defaultExtra = await _settings.GetAsync<int>(SettingKeys.Timesheet.TimeAlertDefaultExtraMinutes, ct);
        var confirmationText = await _settings.GetAsync<string>(SettingKeys.Timesheet.TimeAlertConfirmationText, ct);
        var disablePrompt = await _settings.GetAsync<string>(SettingKeys.Timesheet.TimeAlertDisableReasonPrompt, ct);

        var row = await ReadRowAsync(ticketId, ct);
        if (row is null)
        {
            // Ticket gone — report a disabled, empty snapshot rather than throw.
            return new TicketTimeAlertStatus(
                false, globalThreshold, 0, globalThreshold, 0, globalThreshold, false,
                defaultExtra, confirmationText ?? string.Empty, false, disablePrompt ?? string.Empty);
        }

        // A ticket whose tracking was explicitly disabled never warns, whatever
        // the global/queue resolution says.
        var enabled = !row.TrackingDisabled && ResolveEnabled(row.QueueMode, globalEnabled);
        var threshold = row.QueueThresholdMinutes ?? globalThreshold;
        var limit = threshold + row.ExtraMinutes;
        var remaining = limit - row.TotalMinutes;
        var exceeded = enabled && row.TotalMinutes > limit;

        return new TicketTimeAlertStatus(
            Enabled: enabled,
            ThresholdMinutes: threshold,
            ExtraMinutes: row.ExtraMinutes,
            LimitMinutes: limit,
            TotalMinutes: row.TotalMinutes,
            RemainingMinutes: remaining,
            Exceeded: exceeded,
            DefaultExtraMinutes: defaultExtra,
            ConfirmationText: confirmationText ?? string.Empty,
            TrackingDisabled: row.TrackingDisabled,
            DisableReasonPrompt: disablePrompt ?? string.Empty);
    }

    public async Task DismissAsync(Guid ticketId, Guid actorUserId, CancellationToken ct = default)
    {
        var globalThreshold = await _settings.GetAsync<int>(SettingKeys.Timesheet.TimeAlertThresholdMinutes, ct);
        var row = await ReadRowAsync(ticketId, ct);
        if (row is null) return;

        var threshold = row.QueueThresholdMinutes ?? globalThreshold;
        var limit = threshold + row.ExtraMinutes;

        var metadata = JsonSerializer.Serialize(new
        {
            totalMinutes = row.TotalMinutes,
            limitMinutes = limit,
        });

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // is_internal = TRUE: hour-limit events are back-office only and must
        // never surface on a customer-facing timeline.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'TimeLimitAlertDismissed', @actorUserId, @metadata::jsonb, TRUE)
            """,
            new { ticketId, actorUserId, metadata }, cancellationToken: ct));
    }

    public async Task<TicketTimeAlertExtendResult> ExtendAsync(
        Guid ticketId, Guid actorUserId, int addMinutes, bool customerConfirmed,
        string? note, CancellationToken ct = default)
    {
        if (addMinutes <= 0 || addMinutes > MaxExtendMinutes)
            return TicketTimeAlertExtendResult.InvalidMinutes;
        if (!customerConfirmed)
            return TicketTimeAlertExtendResult.NotConfirmed;

        var globalThreshold = await _settings.GetAsync<int>(SettingKeys.Timesheet.TimeAlertThresholdMinutes, ct);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Raise the per-ticket grant atomically and read back the new total.
        // Returns null when the ticket does not exist.
        var newExtra = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            UPDATE tickets
            SET time_alert_extra_minutes = time_alert_extra_minutes + @addMinutes
            WHERE id = @ticketId
            RETURNING time_alert_extra_minutes
            """,
            new { ticketId, addMinutes }, tx, cancellationToken: ct));

        if (newExtra is null)
        {
            await tx.RollbackAsync(ct);
            return TicketTimeAlertExtendResult.TicketNotFound;
        }

        var row = await conn.QuerySingleAsync<AlertRow>(new CommandDefinition(
            RowSql, new { ticketId }, tx, cancellationToken: ct));

        // The resolved limit uses the queue override when present so the logged
        // "new limit" matches what the agent saw in the dialog.
        var threshold = row.QueueThresholdMinutes ?? globalThreshold;
        var newLimit = threshold + newExtra.Value;
        var previousLimit = newLimit - addMinutes;

        var metadata = JsonSerializer.Serialize(new
        {
            addedMinutes = addMinutes,
            previousLimitMinutes = previousLimit,
            newLimitMinutes = newLimit,
            totalMinutes = row.TotalMinutes,
            customerConfirmed = true,
        });

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'TimeLimitExtended', @actorUserId, @metadata::jsonb, TRUE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        // Optional free-text note (v0.0.88): posted as a normal internal note so
        // it renders in the timeline with the agent as author. Best-effort and
        // after commit — the limit raise stands even if the note insert fails.
        await PostInternalNoteAsync(
            ticketId, actorUserId, "Hour limit raised — note from the agent", note, ct);

        return TicketTimeAlertExtendResult.Ok;
    }

    public async Task<TicketTimeAlertDisableResult> DisableAsync(
        Guid ticketId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return TicketTimeAlertDisableResult.ReasonRequired;

        var globalThreshold = await _settings.GetAsync<int>(SettingKeys.Timesheet.TimeAlertThresholdMinutes, ct);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Flip the per-ticket switch and read back the live snapshot in one go.
        // Returns null when the ticket does not exist.
        var updated = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            UPDATE tickets
            SET time_alert_tracking_disabled = TRUE
            WHERE id = @ticketId
            RETURNING 1
            """,
            new { ticketId }, tx, cancellationToken: ct));

        if (updated is null)
        {
            await tx.RollbackAsync(ct);
            return TicketTimeAlertDisableResult.TicketNotFound;
        }

        var row = await conn.QuerySingleAsync<AlertRow>(new CommandDefinition(
            RowSql, new { ticketId }, tx, cancellationToken: ct));

        var threshold = row.QueueThresholdMinutes ?? globalThreshold;
        var limit = threshold + row.ExtraMinutes;

        var metadata = JsonSerializer.Serialize(new
        {
            totalMinutes = row.TotalMinutes,
            limitMinutes = limit,
            reason = trimmed,
        });

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'TimeLimitTrackingDisabled', @actorUserId, @metadata::jsonb, TRUE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        // The mandatory reason is posted as an internal note (after commit, same
        // best-effort rationale as the extend path).
        await PostInternalNoteAsync(
            ticketId, actorUserId, "Hour tracking disabled for this ticket", trimmed, ct);

        return TicketTimeAlertDisableResult.Ok;
    }

    /// Posts <paramref name="body"/> as a normal internal note on the ticket,
    /// led by a bold <paramref name="heading"/> so the timeline makes clear the
    /// note came from an hour-tracking action (not a free-standing agent note).
    /// No-op when the body is blank. The heading and the user-supplied body are
    /// HTML-encoded before composing the markup; a plain-text fallback mirrors it.
    private async Task PostInternalNoteAsync(
        Guid ticketId, Guid actorUserId, string heading, string? body, CancellationToken ct)
    {
        var trimmed = body?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        var encodedBody = System.Net.WebUtility.HtmlEncode(trimmed)
            .Replace("\r\n", "<br>").Replace("\n", "<br>");
        var bodyHtml =
            $"<p><strong>{System.Net.WebUtility.HtmlEncode(heading)}</strong></p><p>{encodedBody}</p>";
        var bodyText = $"{heading}\n\n{trimmed}";

        try
        {
            await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
                EventType: TicketEventType.Note.ToString(),
                BodyText: bodyText,
                BodyHtml: bodyHtml,
                IsInternal: true,
                AuthorUserId: actorUserId,
                AuthorContactId: null,
                MetadataJson: null), ct);
        }
        catch (Exception ex)
        {
            // The note is secondary — the limit raise / disable already
            // committed and is logged as its own timeline event. Don't fail the
            // whole action just because the note insert hiccupped.
            _logger.LogWarning(ex,
                "Failed to post the internal note for the hour-limit action on ticket {TicketId}", ticketId);
        }
    }

    private static bool ResolveEnabled(string? queueMode, bool globalEnabled) => queueMode switch
    {
        "on" => true,
        "off" => false,
        _ => globalEnabled,
    };

    // NOTE (v0.0.87): the limit deliberately counts ALL logged time on the
    // ticket, ACROSS every agent, and IGNORES the timesheet_entries.invoiced
    // flag. The invoiced flag is not meaningfully used yet, so it plays no
    // role here. REVISIT once invoiced is actually used: confirm with the
    // product owner whether already-invoiced hours should still count toward
    // the per-ticket limit, or only un-invoiced time. See the matching note
    // in memory (project_ticket_hour_limit_invoiced_revisit) and on the
    // invoiced-flag usage sites.
    private const string RowSql = """
        SELECT
            COALESCE((SELECT SUM(e.minutes) FROM timesheet_entries e WHERE e.ticket_id = t.id), 0)::int AS TotalMinutes,
            t.time_alert_extra_minutes      AS ExtraMinutes,
            t.time_alert_tracking_disabled  AS TrackingDisabled,
            q.time_alert_mode               AS QueueMode,
            q.time_alert_threshold_minutes  AS QueueThresholdMinutes
        FROM tickets t
        JOIN queues  q ON q.id = t.queue_id
        WHERE t.id = @ticketId
        """;

    private async Task<AlertRow?> ReadRowAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AlertRow>(new CommandDefinition(
            RowSql, new { ticketId }, cancellationToken: ct));
    }

    private sealed class AlertRow
    {
        public int TotalMinutes { get; init; }
        public int ExtraMinutes { get; init; }
        public bool TrackingDisabled { get; init; }
        public string QueueMode { get; init; } = "inherit";
        public int? QueueThresholdMinutes { get; init; }
    }
}
