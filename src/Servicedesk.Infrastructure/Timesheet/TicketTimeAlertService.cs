using System.Text.Json;
using Dapper;
using Npgsql;
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

    public TicketTimeAlertService(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public async Task<TicketTimeAlertStatus> GetStatusAsync(Guid ticketId, CancellationToken ct = default)
    {
        var globalEnabled = await _settings.GetAsync<bool>(SettingKeys.Timesheet.TimeAlertEnabled, ct);
        var globalThreshold = await _settings.GetAsync<int>(SettingKeys.Timesheet.TimeAlertThresholdMinutes, ct);
        var defaultExtra = await _settings.GetAsync<int>(SettingKeys.Timesheet.TimeAlertDefaultExtraMinutes, ct);
        var confirmationText = await _settings.GetAsync<string>(SettingKeys.Timesheet.TimeAlertConfirmationText, ct);

        var row = await ReadRowAsync(ticketId, ct);
        if (row is null)
        {
            // Ticket gone — report a disabled, empty snapshot rather than throw.
            return new TicketTimeAlertStatus(
                false, globalThreshold, 0, globalThreshold, 0, globalThreshold, false,
                defaultExtra, confirmationText ?? string.Empty);
        }

        var enabled = ResolveEnabled(row.QueueMode, globalEnabled);
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
            ConfirmationText: confirmationText ?? string.Empty);
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
        CancellationToken ct = default)
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
        return TicketTimeAlertExtendResult.Ok;
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
        public string QueueMode { get; init; } = "inherit";
        public int? QueueThresholdMinutes { get; init; }
    }
}
