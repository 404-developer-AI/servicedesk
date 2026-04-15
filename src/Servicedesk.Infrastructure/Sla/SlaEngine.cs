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
}

/// Evaluates SLA state for a ticket and writes it to ticket_sla_state. The
/// engine is idempotent: it's safe to call any hook multiple times — the
/// recomputed state depends only on the ticket + events, not prior state.
public sealed class SlaEngine : ISlaEngine
{
    private static readonly HashSet<string> AllowedTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mail", "Comment", "Note", "StatusChange", "AssignmentChange", "QueueChange"
    };

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

    public async Task RecalcAsync(Guid ticketId, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Load ticket basics + status category + current agent-touch events.
            var ticket = await conn.QueryFirstOrDefaultAsync<TicketCore>(new CommandDefinition("""
                SELECT t.id AS Id, t.queue_id AS QueueId, t.priority_id AS PriorityId, t.status_id AS StatusId,
                       t.created_utc AS CreatedUtc, t.resolved_utc AS ResolvedUtc, t.closed_utc AS ClosedUtc,
                       s.state_category AS StateCategory
                FROM tickets t
                JOIN statuses s ON s.id = t.status_id
                WHERE t.id = @ticketId AND t.is_deleted = FALSE
                """, new { ticketId }, cancellationToken: ct));
            if (ticket is null) return;

            var policy = await _repo.FindPolicyAsync(ticket.QueueId, ticket.PriorityId, ct);
            if (policy is null)
            {
                await _repo.UpsertStateAsync(new TicketSlaState(
                    ticketId, null, null, null, null, null, null, null, false, null, 0,
                    DateTime.UtcNow, DateTime.UtcNow), ct);
                return;
            }

            var schema = await _repo.GetSchemaAsync(policy.BusinessHoursSchemaId, ct);
            if (schema is null)
            {
                _logger.LogWarning("SLA policy {PolicyId} references missing schema {SchemaId}", policy.Id, policy.BusinessHoursSchemaId);
                return;
            }

            // First-response triggers from settings.
            var triggersJson = await _settings.GetAsync<string>(SettingKeys.Sla.FirstContactTriggers, ct);
            var triggers = ParseTriggers(triggersJson);
            var pauseOnPendingSetting = await _settings.GetAsync<bool>(SettingKeys.Sla.PauseOnPending, ct);
            var pauseOnPending = policy.PauseOnPending && pauseOnPendingSetting;

            // First-response detection: earliest agent event with a trigger type.
            var firstResponse = await conn.QueryFirstOrDefaultAsync<DateTime?>(new CommandDefinition("""
                SELECT MIN(created_utc) FROM ticket_events
                WHERE ticket_id = @ticketId
                  AND author_user_id IS NOT NULL
                  AND event_type = ANY(@triggers)
                """, new { ticketId, triggers = triggers.ToArray() }, cancellationToken: ct));

            // Pause bookkeeping: sum business-minutes spent in Pending up to now.
            var pendingPeriods = await LoadPendingPeriodsAsync(conn, ticketId, ct);
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
            var frDeadline = _calculator.AddBusinessMinutes(ticket.CreatedUtc, policy.FirstResponseMinutes, schema);
            var resDeadline = _calculator.AddBusinessMinutes(ticket.CreatedUtc, policy.ResolutionMinutes, schema);
            if (pauseOnPending && pausedAccumMinutes > 0)
            {
                frDeadline = _calculator.AddBusinessMinutes(frDeadline, pausedAccumMinutes, schema);
                resDeadline = _calculator.AddBusinessMinutes(resDeadline, pausedAccumMinutes, schema);
            }

            int? frMinutesConsumed = firstResponse.HasValue
                ? Math.Max(0, _calculator.BusinessMinutesBetween(ticket.CreatedUtc, firstResponse.Value, schema) - (pauseOnPending ? pausedAccumMinutes : 0))
                : null;
            int? resMinutesConsumed = ticket.ResolvedUtc.HasValue
                ? Math.Max(0, _calculator.BusinessMinutesBetween(ticket.CreatedUtc, ticket.ResolvedUtc.Value, schema) - (pauseOnPending ? pausedAccumMinutes : 0))
                : null;

            var state = new TicketSlaState(
                TicketId: ticketId,
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

            await _repo.UpsertStateAsync(state, ct);

            // Mirror first_response_utc onto tickets for backwards compat with legacy queries.
            if (firstResponse.HasValue)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tickets SET first_response_utc = @firstResponse WHERE id = @ticketId AND first_response_utc IS NULL",
                    new { firstResponse, ticketId }, cancellationToken: ct));
            }
            // Mirror deadline onto due_utc so existing dashboards/indexes keep working.
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tickets SET due_utc = @due WHERE id = @ticketId",
                new { due = resDeadline, ticketId }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SLA recalc failed for ticket {TicketId}", ticketId);
        }
    }

    private static async Task<IReadOnlyList<(DateTime Start, DateTime? End)>> LoadPendingPeriodsAsync(NpgsqlConnection conn, Guid ticketId, CancellationToken ct)
    {
        // StatusChange events carry metadata with the new status category. We
        // reconstruct pending-windows from the event stream.
        const string sql = """
            SELECT e.created_utc AS CreatedUtc, e.metadata AS Metadata
            FROM ticket_events e
            WHERE e.ticket_id = @ticketId AND e.event_type = 'StatusChange'
            ORDER BY e.created_utc, e.id
            """;
        var rows = (await conn.QueryAsync<(DateTime CreatedUtc, string Metadata)>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct))).ToList();

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
            return new HashSet<string>(arr.Where(AllowedTriggers.Contains), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(new[] { "Mail", "Comment" }, StringComparer.OrdinalIgnoreCase);
        }
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
        public string StateCategory { get; set; } = "";
    }
}
