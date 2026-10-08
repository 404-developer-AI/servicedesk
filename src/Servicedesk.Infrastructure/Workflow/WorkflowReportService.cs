using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Insights;
using Servicedesk.Infrastructure.Insights.Rewind;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Workflow;

public enum WorkflowKpi { CallBeforeMail, TemplateBeforePending, NoCherryPicking, TimeLimits, ResearchLoad }

/// One checked moment: OK or not, with a plain-English reason. Research-load
/// cases are a period over the limit with no single ticket; they carry the
/// period's length (<see cref="Minutes"/>) and ticket count (<see cref="Count"/>).
public sealed record WorkflowCase(
    WorkflowKpi Kpi, Guid AgentId, Guid? TicketId, long? TicketNumber, string? Subject,
    DateTime AtUtc, bool Ok, string Detail, int? Minutes = null, int? Count = null);

public sealed record WorkflowScore(int Ok, int Total);

public sealed record WorkflowAgentRow(
    Guid AgentId, string Name,
    WorkflowScore CallBeforeMail, WorkflowScore TemplateBeforePending,
    WorkflowScore NoCherryPicking, WorkflowScore TimeLimits,
    int ResearchOverMinutes, int ResearchPeak);

public sealed record WorkflowLimits(
    int MailAfterTemplateMinutes, int PriorityMinutes, int CallbackMinutes, int WfpMinutes, int ResearchMinutes,
    int MaxResearchPerAgent, IReadOnlyList<Guid> WfpStatusIds);

public sealed record WorkflowReport(
    InsightsRange Range, Guid? ViewId, WorkflowLimits Limits, IReadOnlyList<WorkflowAgentRow> Agents);

/// v0.1.32 — Insights Workflow: the servicedesk work order, checked per
/// agent over a period. Every query is cut to the caller's queue access.
/// Pickups, time limits and research load read the chosen Rewind-tracked
/// view (its snapshots and the pickups recorded against it); call-before-mail
/// and template-before-pending do not depend on a view.
public interface IWorkflowReportService
{
    /// Hard guard on the report window, not a tunable.
    public const int MaxDays = 62;

    Task<WorkflowLimits> GetLimitsAsync(CancellationToken ct = default);
    Task<WorkflowReport> GetReportAsync(InsightsRange range, Guid? viewId, QueueAccessScope scope, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowCase>> GetCasesAsync(
        InsightsRange range, Guid? viewId, QueueAccessScope scope, Guid agentId, WorkflowKpi kpi, CancellationToken ct = default);
}

public sealed class WorkflowReportService : IWorkflowReportService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;

    public WorkflowReportService(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public async Task<WorkflowLimits> GetLimitsAsync(CancellationToken ct = default)
    {
        var csv = await GetOrAsync(SettingKeys.Insights.WorkflowWfpStatusIds, "", ct) ?? "";
        var wfp = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        return new WorkflowLimits(
            Math.Clamp(await GetOrAsync(SettingKeys.Insights.WorkflowMailAfterTemplateMinutes, 15, ct), 0, 240),
            Math.Clamp(await GetOrAsync(SettingKeys.Insights.WorkflowLimitPriorityMinutes, 60, ct), 1, 600),
            Math.Clamp(await GetOrAsync(SettingKeys.Insights.WorkflowLimitCallbackMinutes, 15, ct), 1, 600),
            Math.Clamp(await GetOrAsync(SettingKeys.Insights.WorkflowLimitWfpMinutes, 15, ct), 1, 600),
            Math.Clamp(await GetOrAsync(SettingKeys.Insights.WorkflowLimitResearchMinutes, 60, ct), 1, 600),
            Math.Clamp(await GetOrAsync(SettingKeys.Insights.WorkflowMaxResearchPerAgent, 2, ct), 1, 20),
            wfp);
    }

    public async Task<WorkflowReport> GetReportAsync(
        InsightsRange range, Guid? viewId, QueueAccessScope scope, CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(ct);
        var cases = await ComputeAsync(range, viewId, scope, limits, ct);

        var agents = cases.Select(c => c.AgentId).Distinct().ToArray();
        var names = await LoadNamesAsync(agents, ct);

        WorkflowScore Score(IEnumerable<WorkflowCase> cs, WorkflowKpi k)
        {
            var list = cs.Where(c => c.Kpi == k).ToList();
            return new WorkflowScore(list.Count(c => c.Ok), list.Count);
        }

        var rows = cases.GroupBy(c => c.AgentId).Select(g =>
        {
            var research = g.Where(c => c.Kpi == WorkflowKpi.ResearchLoad).ToList();
            return new WorkflowAgentRow(
                g.Key, names.GetValueOrDefault(g.Key, "Unknown user"),
                Score(g, WorkflowKpi.CallBeforeMail), Score(g, WorkflowKpi.TemplateBeforePending),
                Score(g, WorkflowKpi.NoCherryPicking), Score(g, WorkflowKpi.TimeLimits),
                research.Sum(c => c.Minutes ?? 0), research.Count == 0 ? 0 : research.Max(c => c.Count ?? 0));
        }).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new WorkflowReport(range, viewId, limits, rows);
    }

    public async Task<IReadOnlyList<WorkflowCase>> GetCasesAsync(
        InsightsRange range, Guid? viewId, QueueAccessScope scope, Guid agentId, WorkflowKpi kpi, CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(ct);
        var cases = await ComputeAsync(range, viewId, scope, limits, ct, onlyKpi: kpi, onlyAgent: agentId);
        return cases.Where(c => c.AgentId == agentId && c.Kpi == kpi)
            .OrderBy(c => c.Ok).ThenByDescending(c => c.AtUtc).ToList();
    }

    private async Task<List<WorkflowCase>> ComputeAsync(
        InsightsRange range, Guid? viewId, QueueAccessScope scope, WorkflowLimits limits, CancellationToken ct,
        WorkflowKpi? onlyKpi = null, Guid? onlyAgent = null)
    {
        var tz = await ResolveZoneAsync(ct);
        var fromUtc = InsightsCalendar.LocalMidnightUtc(range.From, tz);
        var toUtc = InsightsCalendar.LocalMidnightUtc(range.To.AddDays(1), tz);
        var p = new QueryArgs(fromUtc, toUtc, scope.IsAdmin, scope.ToArray(), onlyAgent);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var cases = new List<WorkflowCase>();
        bool Want(WorkflowKpi k) => onlyKpi is null || onlyKpi == k;

        if (Want(WorkflowKpi.CallBeforeMail)) cases.AddRange(await CallBeforeMailAsync(conn, p, ct));
        if (Want(WorkflowKpi.TemplateBeforePending)) cases.AddRange(await TemplateBeforePendingAsync(conn, p, limits, ct));
        if (viewId is { } v)
        {
            if (Want(WorkflowKpi.NoCherryPicking)) cases.AddRange(await CherryPickingAsync(conn, p, v, limits, tz, ct));
            if (Want(WorkflowKpi.TimeLimits)) cases.AddRange(await TimeLimitsAsync(conn, p, v, limits, range, tz, ct));
            if (Want(WorkflowKpi.ResearchLoad)) cases.AddRange(await ResearchLoadAsync(conn, p, v, limits, ct));
        }
        return cases;
    }

    private sealed record QueryArgs(DateTime FromUtc, DateTime ToUtc, bool IsAdmin, Guid[] QueueIds, Guid? Agent);

    // ---- 1. call before mail -----------------------------------------------------

    private sealed class MailRow
    {
        public Guid AgentId { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string Subject { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public bool HadInbound { get; set; }
        public bool Ok { get; set; }
    }

    /// Every agent-sent mail needs a call on the ticket (by anyone) after
    /// the customer's latest inbound mail; no inbound yet = any call since
    /// the ticket exists. A voicemail is logged as a call, so it counts.
    private static async Task<IEnumerable<WorkflowCase>> CallBeforeMailAsync(NpgsqlConnection conn, QueryArgs p, CancellationToken ct)
    {
        const string sql = """
            SELECT m.author_user_id AS AgentId, m.ticket_id AS TicketId, t.number AS TicketNumber, t.subject AS Subject,
                   m.created_utc AS AtUtc, li.at IS NOT NULL AS HadInbound,
                   EXISTS (SELECT 1 FROM ticket_events c
                           WHERE c.ticket_id = m.ticket_id AND c.event_type = 'Call'
                             AND c.created_utc < m.created_utc
                             AND c.created_utc > COALESCE(li.at, '-infinity'::timestamptz)) AS Ok
            FROM ticket_events m
            JOIN tickets t ON t.id = m.ticket_id AND t.is_deleted = FALSE
            LEFT JOIN LATERAL (SELECT max(r.created_utc) AS at FROM ticket_events r
                               WHERE r.ticket_id = m.ticket_id AND r.event_type = 'MailReceived'
                                 AND r.created_utc < m.created_utc) li ON TRUE
            WHERE m.event_type = 'MailSent' AND m.author_user_id IS NOT NULL
              AND m.created_utc >= @FromUtc AND m.created_utc < @ToUtc
              AND (@IsAdmin OR t.queue_id = ANY(@QueueIds))
              AND (@Agent::uuid IS NULL OR m.author_user_id = @Agent)
            """;
        var rows = await conn.QueryAsync<MailRow>(new CommandDefinition(sql, p, cancellationToken: ct));
        return rows.Select(r => new WorkflowCase(
            WorkflowKpi.CallBeforeMail, r.AgentId, r.TicketId, r.TicketNumber, r.Subject, r.AtUtc, r.Ok,
            r.Ok ? "Called before mailing"
                 : r.HadInbound ? "Mailed without a call since the customer's last mail"
                                : "Mailed without any call on the ticket"));
    }

    // ---- 2. template before pending ------------------------------------------------

    private sealed class MoveRow
    {
        public Guid AgentId { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string Subject { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public bool ViaTrigger { get; set; }
        public string? ToStatus { get; set; }
        public string? LastType { get; set; }
        public bool LastIsWorkflow { get; set; }
        public bool? LastFilled { get; set; }
        public bool MailAfterTemplate { get; set; }
    }

    /// Each move into a Pending-category status by an agent — manual, or a
    /// trigger that ran inside the agent's request (on_behalf_of) — must be
    /// preceded by that agent's last action being a filled-in Workflow
    /// template, or a mail sent shortly after one.
    private static async Task<IEnumerable<WorkflowCase>> TemplateBeforePendingAsync(
        NpgsqlConnection conn, QueryArgs p, WorkflowLimits limits, CancellationToken ct)
    {
        const string sql = """
            WITH moves AS (
                SELECT s.id, s.ticket_id, s.created_utc,
                       COALESCE(s.author_user_id, (s.metadata->>'on_behalf_of')::uuid) AS agent_id,
                       s.author_user_id IS NULL AS via_trigger,
                       s.metadata->>'toName' AS to_status
                FROM ticket_events s
                WHERE s.event_type = 'StatusChange'
                  AND s.metadata->>'toCategory' = 'Pending'
                  AND COALESCE(s.metadata->>'fromCategory', '') <> 'Pending'
                  AND s.created_utc >= @FromUtc AND s.created_utc < @ToUtc
            )
            SELECT mv.agent_id AS AgentId, mv.ticket_id AS TicketId, t.number AS TicketNumber, t.subject AS Subject,
                   mv.created_utc AS AtUtc, mv.via_trigger AS ViaTrigger, mv.to_status AS ToStatus,
                   la.event_type AS LastType, COALESCE(ct.workflow_close, FALSE) AS LastIsWorkflow,
                   la.template_filled AS LastFilled,
                   (la.event_type = 'MailSent' AND EXISTS (
                        SELECT 1 FROM ticket_events w
                        JOIN compose_templates wt ON wt.id = w.compose_template_id AND wt.workflow_close
                        WHERE w.ticket_id = mv.ticket_id AND w.author_user_id = mv.agent_id
                          AND w.template_filled = TRUE
                          AND w.created_utc <= la.created_utc
                          AND w.created_utc >= la.created_utc - make_interval(mins => @Window))) AS MailAfterTemplate
            FROM moves mv
            JOIN tickets t ON t.id = mv.ticket_id AND t.is_deleted = FALSE
            LEFT JOIN LATERAL (
                SELECT a.event_type, a.created_utc, a.compose_template_id, a.template_filled
                FROM ticket_events a
                WHERE a.ticket_id = mv.ticket_id AND a.author_user_id = mv.agent_id
                  AND a.event_type IN ('Note', 'Comment', 'Call', 'MailSent')
                  AND a.created_utc <= mv.created_utc AND a.id <> mv.id
                ORDER BY a.created_utc DESC, a.id DESC
                LIMIT 1) la ON TRUE
            LEFT JOIN compose_templates ct ON ct.id = la.compose_template_id
            WHERE mv.agent_id IS NOT NULL
              AND (@IsAdmin OR t.queue_id = ANY(@QueueIds))
              AND (@Agent::uuid IS NULL OR mv.agent_id = @Agent)
            """;
        var rows = await conn.QueryAsync<MoveRow>(new CommandDefinition(sql,
            new { p.FromUtc, p.ToUtc, p.IsAdmin, p.QueueIds, p.Agent, Window = limits.MailAfterTemplateMinutes },
            cancellationToken: ct));
        return rows.Select(r =>
        {
            var templateOk = r.LastType is "Note" or "Comment" or "Call" && r.LastIsWorkflow && r.LastFilled == true;
            var ok = templateOk || r.MailAfterTemplate;
            var how = r.ViaTrigger ? $"set to {r.ToStatus} by a trigger after the agent's action" : $"set to {r.ToStatus}";
            var detail = ok
                ? (r.MailAfterTemplate ? $"Mail right after a filled-in Workflow template; {how}" : $"Filled-in Workflow template; {how}")
                : r.LastType is null ? $"No note, call or mail by the agent before it was {how}"
                : r.LastType == "MailSent" ? $"Last action was a mail without a Workflow template shortly before; {how}"
                : r.LastIsWorkflow && r.LastFilled != true ? $"Workflow template left unfilled; {how}"
                : $"Last action was a {Lower(r.LastType)} without a Workflow template; {how}";
            return new WorkflowCase(WorkflowKpi.TemplateBeforePending, r.AgentId, r.TicketId, r.TicketNumber, r.Subject,
                r.AtUtc, ok, detail);
        });
    }

    private static string Lower(string? eventType) => eventType switch
    {
        "Comment" => "reply",
        null => "",
        _ => eventType.ToLowerInvariant(),
    };

    // ---- 3. no cherry picking ------------------------------------------------------

    private sealed class PickupRow
    {
        public Guid AgentId { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string Subject { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public string GroupKey { get; set; } = "";
        public string GroupLabel { get; set; } = "";
        public int Position { get; set; }
        public Guid[] AboveIds { get; set; } = Array.Empty<Guid>();
    }

    private sealed class EntrySpan
    {
        public Guid TicketId { get; set; }
        public DateTime EntryDate { get; set; }
        public int StartMinutes { get; set; }
        public int EndMinutes { get; set; }
    }

    /// A pickup in Priority / Call-back / WFP is OK when every ticket above
    /// it in that group was being worked by someone — a registered timesheet
    /// entry on it covering the pickup moment. Research may be picked freely.
    private static async Task<IEnumerable<WorkflowCase>> CherryPickingAsync(
        NpgsqlConnection conn, QueryArgs p, Guid viewId, WorkflowLimits limits, TimeZoneInfo tz, CancellationToken ct)
    {
        const string sql = """
            SELECT w.user_id AS AgentId, w.ticket_id AS TicketId, t.number AS TicketNumber, t.subject AS Subject,
                   w.picked_utc AS AtUtc, w.group_key AS GroupKey, w.group_label AS GroupLabel,
                   w.position AS Position, w.above_ids AS AboveIds
            FROM workflow_pickups w
            JOIN tickets t ON t.id = w.ticket_id AND t.is_deleted = FALSE
            WHERE w.view_id = @ViewId AND w.picked_utc >= @FromUtc AND w.picked_utc < @ToUtc
              AND (@IsAdmin OR t.queue_id = ANY(@QueueIds))
              AND (@Agent::uuid IS NULL OR w.user_id = @Agent)
            """;
        var rows = (await conn.QueryAsync<PickupRow>(new CommandDefinition(sql,
            new { ViewId = viewId, p.FromUtc, p.ToUtc, p.IsAdmin, p.QueueIds, p.Agent }, cancellationToken: ct)))
            .Where(r => GroupKindOf(r.GroupKey, limits) is GroupKind.Priority or GroupKind.Callback or GroupKind.Wfp)
            .ToList();
        if (rows.Count == 0) return Array.Empty<WorkflowCase>();

        var aboveIds = rows.SelectMany(r => r.AboveIds).Distinct().ToArray();
        var dates = rows.Select(r => LocalDate(r.AtUtc, tz)).Distinct().Select(d => d.ToDateTime(TimeOnly.MinValue)).ToArray();
        var spans = aboveIds.Length == 0 ? new List<EntrySpan>() : (await conn.QueryAsync<EntrySpan>(new CommandDefinition(
            """
            SELECT ticket_id AS TicketId, entry_date AS EntryDate, start_minutes AS StartMinutes, end_minutes AS EndMinutes
            FROM timesheet_entries
            WHERE ticket_id = ANY(@aboveIds) AND entry_date = ANY(@dates)
            """, new { aboveIds, dates }, cancellationToken: ct))).ToList();
        // Ticket numbers only for tickets the caller may see.
        var numbers = aboveIds.Length == 0 ? new Dictionary<Guid, long>() : (await conn.QueryAsync<(Guid Id, long Number)>(new CommandDefinition(
            """
            SELECT id, number FROM tickets
            WHERE id = ANY(@aboveIds) AND (@IsAdmin OR queue_id = ANY(@QueueIds))
            """, new { aboveIds, p.IsAdmin, p.QueueIds }, cancellationToken: ct))).ToDictionary(x => x.Id, x => x.Number);

        return rows.Select(r =>
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, tz);
            var date = DateOnly.FromDateTime(local);
            var minute = local.Hour * 60 + local.Minute;
            var skipped = r.AboveIds.Where(id => !spans.Any(s =>
                s.TicketId == id && DateOnly.FromDateTime(s.EntryDate) == date
                && s.StartMinutes <= minute && minute < s.EndMinutes)).ToList();
            var ok = skipped.Count == 0;
            string detail;
            if (ok)
            {
                detail = r.Position == 1
                    ? $"Took the top ticket in {r.GroupLabel}"
                    : $"Position {r.Position} in {r.GroupLabel}; every ticket above was being worked";
            }
            else
            {
                var shown = skipped.Where(numbers.ContainsKey).Select(id => $"#{numbers[id]}").ToList();
                var hidden = skipped.Count - shown.Count;
                var list = string.Join(", ", shown) + (hidden > 0 ? $"{(shown.Count > 0 ? " and " : "")}{hidden} in other queues" : "");
                detail = $"Position {r.Position} in {r.GroupLabel}; skipped {list}";
            }
            return new WorkflowCase(WorkflowKpi.NoCherryPicking, r.AgentId, r.TicketId, r.TicketNumber, r.Subject,
                r.AtUtc, ok, detail);
        }).ToList();
    }

    // ---- 4. time limits -------------------------------------------------------------

    private enum GroupKind { None, Priority, Callback, Research, Wfp }

    private static GroupKind GroupKindOf(string key, WorkflowLimits limits) => key switch
    {
        RewindArranger.KeyPriority => GroupKind.Priority,
        RewindArranger.KeyCallback => GroupKind.Callback,
        RewindArranger.KeyResearch => GroupKind.Research,
        _ when limits.WfpStatusIds.Count == 0 => GroupKind.Wfp,
        _ when Guid.TryParse(key, out var status) && limits.WfpStatusIds.Contains(status) => GroupKind.Wfp,
        _ => GroupKind.None,
    };

    private sealed class EntryRow
    {
        public Guid AgentId { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string Subject { get; set; } = "";
        public DateTime EntryDate { get; set; }
        public int StartMinutes { get; set; }
        public int Minutes { get; set; }
    }

    /// Registered time per agent per ticket per group (the group the ticket
    /// showed under in the view when each entry started, from the Rewind
    /// snapshots), summed over the period. Over the limit, the work order
    /// expects Call-back / WFP to move to Research and High prio / Research
    /// to get a Specialist Consult note — any time after the work started.
    private static async Task<IEnumerable<WorkflowCase>> TimeLimitsAsync(
        NpgsqlConnection conn, QueryArgs p, Guid viewId, WorkflowLimits limits, InsightsRange range,
        TimeZoneInfo tz, CancellationToken ct)
    {
        const string entriesSql = """
            SELECT e.user_id AS AgentId, e.ticket_id AS TicketId, t.number AS TicketNumber, t.subject AS Subject,
                   e.entry_date AS EntryDate, e.start_minutes AS StartMinutes, e.minutes AS Minutes
            FROM timesheet_entries e
            JOIN tickets t ON t.id = e.ticket_id AND t.is_deleted = FALSE
            WHERE e.ticket_id IS NOT NULL AND e.entry_date >= @From AND e.entry_date <= @To
              AND (@IsAdmin OR t.queue_id = ANY(@QueueIds))
              AND (@Agent::uuid IS NULL OR e.user_id = @Agent)
            """;
        var entries = (await conn.QueryAsync<EntryRow>(new CommandDefinition(entriesSql, new
        {
            From = range.From.ToDateTime(TimeOnly.MinValue), To = range.To.ToDateTime(TimeOnly.MinValue),
            p.IsAdmin, p.QueueIds, p.Agent,
        }, cancellationToken: ct))).ToList();
        if (entries.Count == 0) return Array.Empty<WorkflowCase>();

        var starts = entries.Select(e => LocalToUtc(DateOnly.FromDateTime(e.EntryDate), e.StartMinutes, tz)).ToArray();
        // The group each entry's ticket showed under at that moment — one
        // indexed snapshot row per entry, matched inside its items.
        const string groupSql = """
            SELECT x.idx AS Idx,
                   (SELECT e->>'group'
                    FROM rewind_snapshots s
                    CROSS JOIN LATERAL jsonb_array_elements(s.items) e
                    WHERE s.view_id = @ViewId AND s.captured_utc <= x.at
                      AND x.at < s.last_seen_utc + make_interval(mins => s.interval_minutes)
                      AND e->>'id' = x.tid::text
                    ORDER BY s.captured_utc DESC
                    LIMIT 1) AS GroupKey
            FROM unnest(@Ats::timestamptz[], @Tids::uuid[]) WITH ORDINALITY AS x(at, tid, idx)
            """;
        var groups = (await conn.QueryAsync<(long Idx, string? GroupKey)>(new CommandDefinition(groupSql, new
        {
            ViewId = viewId, Ats = starts, Tids = entries.Select(e => e.TicketId).ToArray(),
        }, cancellationToken: ct))).ToDictionary(g => (int)g.Idx - 1, g => g.GroupKey);

        var worked = entries.Select((e, i) => (Entry: e, Start: starts[i],
                Kind: groups.TryGetValue(i, out var key) && key is not null ? GroupKindOf(key, limits) : GroupKind.None))
            .Where(x => x.Kind != GroupKind.None)
            .GroupBy(x => (x.Entry.AgentId, x.Entry.TicketId, x.Kind))
            .Select(g => (g.Key.AgentId, g.Key.TicketId, g.Key.Kind, First: g.First().Entry,
                FirstStart: g.Min(x => x.Start), Minutes: g.Sum(x => x.Entry.Minutes)))
            .ToList();
        if (worked.Count == 0) return Array.Empty<WorkflowCase>();

        int Limit(GroupKind k) => k switch
        {
            GroupKind.Priority => limits.PriorityMinutes,
            GroupKind.Callback => limits.CallbackMinutes,
            GroupKind.Research => limits.ResearchMinutes,
            _ => limits.WfpMinutes,
        };
        var over = worked.Where(w => w.Minutes > Limit(w.Kind)).ToList();

        var researchMoves = new HashSet<(Guid, DateTime)>();
        var consults = new List<(Guid TicketId, DateTime At)>();
        if (over.Count > 0)
        {
            var ids = over.Select(o => o.TicketId).Distinct().ToArray();
            var since = over.Min(o => o.FirstStart);
            var followUps = await conn.QueryAsync<(Guid TicketId, DateTime At, bool IsConsult)>(new CommandDefinition(
                """
                SELECT e.ticket_id, e.created_utc, FALSE
                FROM ticket_events e
                WHERE e.ticket_id = ANY(@ids) AND e.created_utc >= @since
                  AND e.event_type = 'TicketFlagChange'
                  AND e.metadata->>'flag' = 'research' AND e.metadata->>'to' = 'true'
                UNION ALL
                SELECT e.ticket_id, e.created_utc, TRUE
                FROM ticket_events e
                JOIN compose_templates c ON c.id = e.compose_template_id AND c.specialist_consult
                WHERE e.ticket_id = ANY(@ids) AND e.created_utc >= @since
                """, new { ids, since }, cancellationToken: ct));
            foreach (var f in followUps)
            {
                if (f.IsConsult) consults.Add((f.TicketId, f.At));
                else researchMoves.Add((f.TicketId, f.At));
            }
        }

        return worked.Select(w =>
        {
            var limit = Limit(w.Kind);
            var label = w.Kind switch
            {
                GroupKind.Priority => "High prio",
                GroupKind.Callback => "Call-back",
                GroupKind.Research => "Research",
                _ => "WFP",
            };
            if (w.Minutes <= limit)
                return new WorkflowCase(WorkflowKpi.TimeLimits, w.AgentId, w.TicketId, w.First.TicketNumber, w.First.Subject,
                    w.FirstStart, true, $"{label}: {w.Minutes} min (limit {limit})");

            var toResearch = w.Kind is GroupKind.Callback or GroupKind.Wfp;
            var handled = toResearch
                ? researchMoves.Any(m => m.Item1 == w.TicketId && m.Item2 >= w.FirstStart)
                : consults.Any(c => c.TicketId == w.TicketId && c.At >= w.FirstStart);
            var expected = toResearch ? "moved to Research" : "Specialist Consult";
            return new WorkflowCase(WorkflowKpi.TimeLimits, w.AgentId, w.TicketId, w.First.TicketNumber, w.First.Subject,
                w.FirstStart, handled,
                $"{label}: {w.Minutes} min (limit {limit}) — {(handled ? expected : $"no {(toResearch ? "move to Research" : "Specialist Consult")}")}");
        }).ToList();
    }

    // ---- 5. research load ---------------------------------------------------------------

    private sealed class LoadRow
    {
        public Guid AgentId { get; set; }
        public DateTime CapturedUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int IntervalMinutes { get; set; }
        public int N { get; set; }
        public long[] Numbers { get; set; } = Array.Empty<long>();
    }

    /// Periods in the view's snapshots where an agent had more Research
    /// tickets assigned than allowed.
    private static async Task<IEnumerable<WorkflowCase>> ResearchLoadAsync(
        NpgsqlConnection conn, QueryArgs p, Guid viewId, WorkflowLimits limits, CancellationToken ct)
    {
        const string sql = """
            SELECT (e->>'assigneeUserId')::uuid AS AgentId, s.captured_utc AS CapturedUtc, s.last_seen_utc AS LastSeenUtc,
                   s.interval_minutes AS IntervalMinutes, count(*)::int AS N,
                   array_agg((e->>'number')::bigint ORDER BY (e->>'number')::bigint) AS Numbers
            FROM rewind_snapshots s
            CROSS JOIN LATERAL jsonb_array_elements(s.items) e
            WHERE s.view_id = @ViewId
              AND s.captured_utc < @ToUtc
              AND s.last_seen_utc + make_interval(mins => s.interval_minutes) > @FromUtc
              AND (e->>'isResearch')::boolean
              AND e->>'assigneeUserId' IS NOT NULL
              AND (@IsAdmin OR (e->>'queueId')::uuid = ANY(@QueueIds))
              AND (@Agent::uuid IS NULL OR (e->>'assigneeUserId')::uuid = @Agent)
            GROUP BY 1, s.id, s.captured_utc, s.last_seen_utc, s.interval_minutes
            HAVING count(*) > @Max
            """;
        var rows = await conn.QueryAsync<LoadRow>(new CommandDefinition(sql,
            new { ViewId = viewId, p.FromUtc, p.ToUtc, p.IsAdmin, p.QueueIds, p.Agent, Max = limits.MaxResearchPerAgent },
            cancellationToken: ct));
        return rows.Select(r =>
        {
            var start = r.CapturedUtc > p.FromUtc ? r.CapturedUtc : p.FromUtc;
            var endCovered = r.LastSeenUtc.AddMinutes(r.IntervalMinutes);
            var end = endCovered < p.ToUtc ? endCovered : p.ToUtc;
            var minutes = Math.Max(0, (int)Math.Round((end - start).TotalMinutes));
            var list = string.Join(", ", r.Numbers.Select(n => $"#{n}"));
            return new WorkflowCase(WorkflowKpi.ResearchLoad, r.AgentId, null, null, null,
                start, false, $"{r.N} Research tickets assigned (max {limits.MaxResearchPerAgent}) for {minutes} min: {list}",
                Minutes: minutes, Count: r.N);
        }).Where(c => c.Minutes > 0).ToList();
    }

    // ---- helpers -------------------------------------------------------------------------

    private async Task<Dictionary<Guid, string>> LoadNamesAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return new Dictionary<Guid, string>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, string Name)>(new CommandDefinition(
            "SELECT id, COALESCE(NULLIF(display_name, ''), email::text) FROM users WHERE id = ANY(@ids)",
            new { ids }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    private async Task<TimeZoneInfo> ResolveZoneAsync(CancellationToken ct)
        => InsightsCalendar.ResolveTimeZone(await GetOrAsync<string?>(SettingKeys.App.TimeZone, null, ct));

    private static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz));

    internal static DateTime LocalToUtc(DateOnly date, int minutes, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes), DateTimeKind.Unspecified);
        // A local time inside a DST gap does not exist; nudge forward an hour.
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    private async Task<T> GetOrAsync<T>(string key, T fallback, CancellationToken ct)
    {
        try { return await _settings.GetAsync<T>(key, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return fallback; }
    }
}
