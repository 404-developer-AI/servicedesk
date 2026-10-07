using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Domain.Sla;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Sla;

public interface ISlaEngine
{
    Task OnTicketCreatedAsync(Guid ticketId, CancellationToken ct);
    Task OnTicketEventAsync(Guid ticketId, string eventType, CancellationToken ct);
    Task OnTicketFieldsChangedAsync(Guid ticketId, CancellationToken ct);
    Task RecalcAsync(Guid ticketId, CancellationToken ct);

    /// v0.1.25 — the periodic sweep's bulk path: the same calculation as
    /// <see cref="RecalcAsync"/> for a whole batch, with one read round-trip
    /// and one write round-trip instead of a few per ticket. Returns the
    /// number of tickets evaluated.
    async Task<int> RecalcManyAsync(IReadOnlyList<Guid> ticketIds, CancellationToken ct)
    {
        foreach (var id in ticketIds) await RecalcAsync(id, ct);
        return ticketIds.Count;
    }

    /// v0.1.25 — the <c>Sla.Enabled</c> kill switch. Off = no calculation
    /// anywhere (ticket mutations, the sweep) and no SLA escalation triggers.
    Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(true);
}

/// Evaluates SLA state for a ticket and writes it to ticket_sla_state. The
/// engine is idempotent: it's safe to call any hook multiple times — the
/// recomputed state depends only on the ticket + events, not prior state.
public sealed class SlaEngine : ISlaEngine
{
    private static readonly HashSet<string> AllowedTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mail", "Comment", "Note", "Call", "StatusChange", "AssignmentChange", "QueueChange"
    };

    // The settings UI exposes "Mail" but OutboundMailService writes "MailSent" on
    // the ticket_events row. Translate the user-facing key to the actual event type
    // so the FR-detection SQL finds it. Inbound mail ("MailReceived") is excluded
    // by the author_user_id IS NOT NULL filter — never an agent touch.
    private static string ToEventType(string trigger)
        => string.Equals(trigger, "Mail", StringComparison.OrdinalIgnoreCase) ? "MailSent" : trigger;

    private const string TicketCoreSelect = """
        SELECT t.id AS Id, t.queue_id AS QueueId, t.priority_id AS PriorityId, t.status_id AS StatusId,
               t.created_utc AS CreatedUtc, t.resolved_utc AS ResolvedUtc, t.closed_utc AS ClosedUtc,
               t.due_utc AS DueUtc, t.first_response_utc AS FirstResponseUtc,
               s.state_category AS StateCategory
        FROM tickets t
        JOIN statuses s ON s.id = t.status_id

        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISlaRepository _repo;
    private readonly IBusinessHoursCalculator _calculator;
    private readonly ISettingsService _settings;
    private readonly ILogger<SlaEngine> _logger;

    public SlaEngine(
        NpgsqlDataSource dataSource,
        ISlaRepository repo,
        IBusinessHoursCalculator calculator,
        ISettingsService settings,
        ILogger<SlaEngine> logger)
    {
        _dataSource = dataSource;
        _repo = repo;
        _calculator = calculator;
        _settings = settings;
        _logger = logger;
    }

    public Task OnTicketCreatedAsync(Guid ticketId, CancellationToken ct) => RecalcAsync(ticketId, ct);
    public Task OnTicketEventAsync(Guid ticketId, string eventType, CancellationToken ct) => RecalcAsync(ticketId, ct);
    public Task OnTicketFieldsChangedAsync(Guid ticketId, CancellationToken ct) => RecalcAsync(ticketId, ct);

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        try { return await _settings.GetAsync<bool>(SettingKeys.Sla.Enabled, ct); }
        catch { return true; }
    }

    public async Task RecalcAsync(Guid ticketId, CancellationToken ct)
    {
        try
        {
            if (!await IsEnabledAsync(ct)) return;
            var cfg = await ReadConfigAsync(ct);

            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // v0.0.101 — one round-trip for everything per-ticket the engine
            // reads: ticket core + status category, earliest agent touch of a
            // trigger type (first response), and the StatusChange stream the
            // pause windows are rebuilt from. Policy + schema come from the
            // repository's in-process config snapshot, not from the DB.
            const string batchSql = TicketCoreSelect + """
                WHERE t.id = @ticketId AND t.is_deleted = FALSE;

                SELECT MIN(created_utc) FROM ticket_events
                WHERE ticket_id = @ticketId
                  AND author_user_id IS NOT NULL
                  AND event_type = ANY(@triggers);

                SELECT e.created_utc AS CreatedUtc, e.metadata AS Metadata
                FROM ticket_events e
                WHERE e.ticket_id = @ticketId AND e.event_type = 'StatusChange'
                ORDER BY e.created_utc, e.id
                """;
            TicketCore? ticket;
            DateTime? firstResponse;
            IReadOnlyList<(DateTime Start, DateTime? End)> pendingPeriods;
            await using (var grid = await conn.QueryMultipleAsync(new CommandDefinition(
                batchSql, new { ticketId, triggers = cfg.Triggers }, cancellationToken: ct)))
            {
                ticket = await grid.ReadFirstOrDefaultAsync<TicketCore>();
                firstResponse = await grid.ReadFirstOrDefaultAsync<DateTime?>();
                var statusRows = (await grid.ReadAsync<(DateTime CreatedUtc, string Metadata)>()).ToList();
                pendingPeriods = BuildPendingPeriods(statusRows);
            }
            if (ticket is null) return;

            var result = await ComputeAsync(ticket, firstResponse, pendingPeriods, cfg, ct);
            if (result is null) return;

            await _repo.UpsertStateAsync(result.State, ct);

            // Mirror first_response_utc onto tickets for backwards compat with legacy queries.
            if (result.MirrorFirstResponse is { } fr)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tickets SET first_response_utc = @firstResponse WHERE id = @ticketId AND first_response_utc IS NULL",
                    new { firstResponse = fr, ticketId }, cancellationToken: ct));
            }
            // Mirror deadline onto due_utc so existing dashboards/indexes keep
            // working — v0.0.101: only when it actually moved. `tickets` is
            // the widest, most-indexed table in the schema; an unconditional
            // UPDATE per ticket per sweep cycle was the single biggest source
            // of dead tuples on it.
            if (result.DueChanged)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tickets SET due_utc = @due WHERE id = @ticketId AND due_utc IS DISTINCT FROM @due",
                    new { due = result.Due, ticketId }, cancellationToken: ct));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SLA recalc failed for ticket {TicketId}", ticketId);
        }
    }

    public async Task<int> RecalcManyAsync(IReadOnlyList<Guid> ticketIds, CancellationToken ct)
    {
        if (ticketIds.Count == 0 || !await IsEnabledAsync(ct)) return 0;
        var cfg = await ReadConfigAsync(ct);
        var ids = ticketIds.Distinct().ToArray();

        // v0.1.25 — the sweep used to call RecalcAsync per ticket: ~3 round
        // trips each, ~27k recalcs an hour at ~470 open tickets, and the
        // Performance monitor showed API latency rising during every sweep.
        // Same reads as the single path, for the whole batch in one
        // round-trip. The first-response probe stays a per-ticket scalar
        // subquery so each one is the same backward index probe as before.
        const string sql = TicketCoreSelect + """
            WHERE t.id = ANY(@ids) AND t.is_deleted = FALSE;

            SELECT i.id AS TicketId,
                   (SELECT MIN(e.created_utc) FROM ticket_events e
                     WHERE e.ticket_id = i.id
                       AND e.author_user_id IS NOT NULL
                       AND e.event_type = ANY(@triggers)) AS FirstResponseUtc
            FROM unnest(@ids) AS i(id);

            SELECT e.ticket_id AS TicketId, e.created_utc AS CreatedUtc, e.metadata AS Metadata
            FROM ticket_events e
            WHERE e.ticket_id = ANY(@ids) AND e.event_type = 'StatusChange'
            ORDER BY e.ticket_id, e.created_utc, e.id
            """;

        List<TicketCore> tickets;
        Dictionary<Guid, DateTime?> firstResponses;
        Dictionary<Guid, List<(DateTime CreatedUtc, string Metadata)>> statusRows;
        await using (var conn = await _dataSource.OpenConnectionAsync(ct))
        await using (var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            sql, new { ids, triggers = cfg.Triggers }, cancellationToken: ct)))
        {
            tickets = (await grid.ReadAsync<TicketCore>()).ToList();
            firstResponses = (await grid.ReadAsync<FirstResponseRow>())
                .ToDictionary(r => r.TicketId, r => r.FirstResponseUtc);
            statusRows = (await grid.ReadAsync<StatusRow>())
                .GroupBy(r => r.TicketId)
                .ToDictionary(g => g.Key, g => g.Select(r => (r.CreatedUtc, r.Metadata)).ToList());
        }

        var writes = new List<SlaStateWrite>(tickets.Count);
        foreach (var ticket in tickets)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                firstResponses.TryGetValue(ticket.Id, out var fr);
                var periods = BuildPendingPeriods(
                    statusRows.TryGetValue(ticket.Id, out var rows) ? rows : new List<(DateTime, string)>());
                var result = await ComputeAsync(ticket, fr, periods, cfg, ct);
                if (result is null) continue;
                writes.Add(new SlaStateWrite(result.State, result.MirrorFirstResponse, result.DueChanged, result.Due));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SLA recalc failed for ticket {TicketId}", ticket.Id);
            }
        }

        // One round-trip for every write of the batch; the state upsert
        // still no-ops server-side when nothing but the timestamps moved.
        await _repo.WriteStatesAsync(writes, ct);
        return tickets.Count;
    }

    private sealed record EngineConfig(string[] Triggers, bool PauseOnPending, TimeSpan CacheAge);

    private sealed record ComputedSla(TicketSlaState State, DateTime? MirrorFirstResponse, bool DueChanged, DateTime? Due);

    private async Task<EngineConfig> ReadConfigAsync(CancellationToken ct)
    {
        // Settings are in-memory (SettingsService primes once); read them
        // first so the DB batch can carry the trigger list.
        var triggersJson = await _settings.GetAsync<string>(SettingKeys.Sla.FirstContactTriggers, ct);
        var pauseOnPendingSetting = await _settings.GetAsync<bool>(SettingKeys.Sla.PauseOnPending, ct);
        var cacheSeconds = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Sla.ConfigCacheSeconds, ct), 0, 3600);
        return new EngineConfig(ParseTriggers(triggersJson).ToArray(), pauseOnPendingSetting, TimeSpan.FromSeconds(cacheSeconds));
    }

    /// The SLA calculation itself — pure apart from the policy lookup (an
    /// in-process snapshot). Shared by the single-ticket hooks and the sweep
    /// so the two can never drift. Null = nothing to write (the policy
    /// points at a missing schema; logged).
    private async Task<ComputedSla?> ComputeAsync(
        TicketCore ticket, DateTime? firstResponse,
        IReadOnlyList<(DateTime Start, DateTime? End)> pendingPeriods,
        EngineConfig cfg, CancellationToken ct)
    {
        var resolved = await _repo.ResolvePolicyAsync(ticket.QueueId, ticket.PriorityId, cfg.CacheAge, ct);
        if (resolved is null)
        {
            var empty = new TicketSlaState(
                ticket.Id, null, null, null, null, null, null, null, false, null, 0,
                DateTime.UtcNow, DateTime.UtcNow);
            return new ComputedSla(empty, null, ticket.DueUtc is not null, null);
        }
        var policy = resolved.Policy;
        var schema = resolved.Schema;
        if (schema is null)
        {
            _logger.LogWarning("SLA policy {PolicyId} references missing schema {SchemaId}", policy.Id, policy.BusinessHoursSchemaId);
            return null;
        }

        var pauseOnPending = policy.PauseOnPending && cfg.PauseOnPending;
        var isCurrentlyPending = pauseOnPending && string.Equals(ticket.StateCategory, "Pending", StringComparison.OrdinalIgnoreCase);
        var pausedAccumMinutes = 0;
        DateTime? pausedSince = null;
        foreach (var (start, end) in pendingPeriods)
        {
            var effectiveEnd = end ?? DateTime.UtcNow;
            pausedAccumMinutes += _calculator.BusinessMinutesBetween(start, effectiveEnd, schema);
            if (end is null) pausedSince = start;
        }

        // Deadlines = created + target, plus accumulated pause time (shift deadline forward).
        // Either target may be null when the policy only tracks one SLA metric.
        DateTime? frDeadline = policy.FirstResponseMinutes.HasValue
            ? _calculator.AddBusinessMinutes(ticket.CreatedUtc, policy.FirstResponseMinutes.Value, schema)
            : null;
        DateTime? resDeadline = policy.ResolutionMinutes.HasValue
            ? _calculator.AddBusinessMinutes(ticket.CreatedUtc, policy.ResolutionMinutes.Value, schema)
            : null;
        if (pauseOnPending && pausedAccumMinutes > 0)
        {
            if (frDeadline.HasValue)
                frDeadline = _calculator.AddBusinessMinutes(frDeadline.Value, pausedAccumMinutes, schema);
            if (resDeadline.HasValue)
                resDeadline = _calculator.AddBusinessMinutes(resDeadline.Value, pausedAccumMinutes, schema);
        }

        int? frMinutesConsumed = firstResponse.HasValue && policy.FirstResponseMinutes.HasValue
            ? Math.Max(0, _calculator.BusinessMinutesBetween(ticket.CreatedUtc, firstResponse.Value, schema) - (pauseOnPending ? pausedAccumMinutes : 0))
            : null;
        int? resMinutesConsumed = ticket.ResolvedUtc.HasValue && policy.ResolutionMinutes.HasValue
            ? Math.Max(0, _calculator.BusinessMinutesBetween(ticket.CreatedUtc, ticket.ResolvedUtc.Value, schema) - (pauseOnPending ? pausedAccumMinutes : 0))
            : null;

        var state = new TicketSlaState(
            TicketId: ticket.Id,
            PolicyId: policy.Id,
            FirstResponseDeadlineUtc: frDeadline,
            ResolutionDeadlineUtc: resDeadline,
            FirstResponseMetUtc: firstResponse,
            ResolutionMetUtc: ticket.ResolvedUtc,
            FirstResponseBusinessMinutes: frMinutesConsumed,
            ResolutionBusinessMinutes: resMinutesConsumed,
            IsPaused: isCurrentlyPending,
            PausedSinceUtc: pausedSince,
            PausedAccumMinutes: pausedAccumMinutes,
            LastRecalcUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow);

        var mirrorFirstResponse = firstResponse.HasValue && ticket.FirstResponseUtc is null ? firstResponse : null;
        return new ComputedSla(state, mirrorFirstResponse, !SameInstant(ticket.DueUtc, resDeadline), resDeadline);
    }

    // Postgres timestamptz carries microseconds; .NET DateTime ticks are
    // 100 ns. Compare at the precision that survives the round-trip so an
    // unchanged deadline is recognised as unchanged.
    private static bool SameInstant(DateTime? a, DateTime? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return Math.Abs((a.Value - b.Value).Ticks) < TimeSpan.TicksPerMillisecond;
    }

    private static IReadOnlyList<(DateTime Start, DateTime? End)> BuildPendingPeriods(
        IReadOnlyList<(DateTime CreatedUtc, string Metadata)> rows)
    {
        // StatusChange events carry metadata with the new status category. We
        // reconstruct pending-windows from the event stream.
        var result = new List<(DateTime Start, DateTime? End)>();
        DateTime? openStart = null;
        foreach (var row in rows)
        {
            string? newCategory = null;
            try
            {
                using var doc = JsonDocument.Parse(row.Metadata);
                if (doc.RootElement.TryGetProperty("toCategory", out var cat)) newCategory = cat.GetString();
                else if (doc.RootElement.TryGetProperty("to_category", out var cat2)) newCategory = cat2.GetString();
            }
            catch { /* malformed metadata → skip */ }

            var isPending = string.Equals(newCategory, "Pending", StringComparison.OrdinalIgnoreCase);
            if (isPending && openStart is null)
            {
                openStart = row.CreatedUtc;
            }
            else if (!isPending && openStart is not null)
            {
                result.Add((openStart.Value, row.CreatedUtc));
                openStart = null;
            }
        }
        if (openStart is not null) result.Add((openStart.Value, null));
        return result;
    }

    private static HashSet<string> ParseTriggers(string json)
    {
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            return new HashSet<string>(
                arr.Where(AllowedTriggers.Contains).Select(ToEventType),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(new[] { "MailSent", "Comment" }, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class FirstResponseRow
    {
        public Guid TicketId { get; set; }
        public DateTime? FirstResponseUtc { get; set; }
    }

    private sealed class StatusRow
    {
        public Guid TicketId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Metadata { get; set; } = "";
    }

    private sealed class TicketCore
    {
        public Guid Id { get; set; }
        public Guid QueueId { get; set; }
        public Guid PriorityId { get; set; }
        public Guid StatusId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? ResolvedUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }
        public DateTime? DueUtc { get; set; }
        public DateTime? FirstResponseUtc { get; set; }
        public string StateCategory { get; set; } = "";
    }
}
