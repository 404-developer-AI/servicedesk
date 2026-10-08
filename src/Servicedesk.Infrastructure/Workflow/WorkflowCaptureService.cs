using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.Insights;
using Servicedesk.Infrastructure.Insights.Rewind;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Workflow;

/// v0.1.32 — Insights Workflow, step 1: records the facts the work-order
/// report reads later. Both calls are best-effort and swallow failures — an
/// agent's note, call or mail must never fail because the report could not
/// record something about it.
public interface IWorkflowCaptureService
{
    /// Detects the compose template a Note/Comment/Call was built from
    /// (see <see cref="WorkflowTemplateDetector"/>) and freezes it on the
    /// event: <c>compose_template_id</c> + <c>template_filled</c>. Called
    /// after create and after every edit (an edit can fill the template in).
    Task RecordTemplateUseAsync(
        Guid ticketId, long eventId, string eventType, string? bodyHtml, string? agentEmail, CancellationToken ct);

    /// Called just <em>before</em> an agent's note/call/mail is written, so
    /// the position is the one the agent picked the ticket from (a logged
    /// call clears Call-back, which would already move it). Records the
    /// agent's first such action on the ticket per local day (App.TimeZone):
    /// for every tracked view the ticket is in, its group, position and the
    /// tickets above it in that group.
    Task RecordPickupAsync(Guid userId, Guid ticketId, string action, CancellationToken ct);
}

public sealed class WorkflowCaptureService : IWorkflowCaptureService
{
    /// Budget for the pickup capture (it runs on the agent's request);
    /// past it the pickup is skipped, never the agent's action.
    private static readonly TimeSpan PickupBudget = TimeSpan.FromSeconds(3);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IComposeTokenResolver _tokens;
    private readonly IRewindStore _rewind;
    private readonly IRewindLayoutService _layout;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkflowCaptureService> _logger;

    public WorkflowCaptureService(
        NpgsqlDataSource dataSource, IComposeTokenResolver tokens, IRewindStore rewind,
        IRewindLayoutService layout, ISettingsService settings, TimeProvider time,
        ILogger<WorkflowCaptureService> logger)
    {
        _dataSource = dataSource;
        _tokens = tokens;
        _rewind = rewind;
        _layout = layout;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    public async Task RecordTemplateUseAsync(
        Guid ticketId, long eventId, string eventType, string? bodyHtml, string? agentEmail, CancellationToken ct)
    {
        // Constant column per kind — never input.
        var scopeColumn = eventType switch
        {
            "Note" or "Comment" => "use_for_note",
            "Call" => "use_for_call",
            _ => null,
        };
        if (scopeColumn is null) return;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            // Every active template in scope, not only the flagged ones: the
            // Workflow / Specialist Consult flags are read at report time, so
            // flagging a template later still counts its earlier use.
            var candidates = (await conn.QueryAsync<WorkflowTemplateCandidate>(new CommandDefinition(
                $"SELECT id AS Id, body_html AS BodyHtml FROM compose_templates WHERE is_active = TRUE AND {scopeColumn} = TRUE",
                cancellationToken: ct))).ToList();

            WorkflowTemplateMatch? match = null;
            if (candidates.Count > 0 && !string.IsNullOrWhiteSpace(bodyHtml))
            {
                var tokens = await _tokens.ResolveForTicketAsync(ticketId, agentEmail, ct);
                match = WorkflowTemplateDetector.Detect(bodyHtml, candidates, tokens);
            }

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE ticket_events
                SET compose_template_id = @templateId, template_filled = @filled
                WHERE id = @eventId AND ticket_id = @ticketId
                  AND (compose_template_id IS DISTINCT FROM @templateId OR template_filled IS DISTINCT FROM @filled)
                """,
                new { eventId, ticketId, templateId = match?.TemplateId, filled = match?.Filled },
                cancellationToken: ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow template detection failed for event {EventId}.", eventId);
        }
    }

    public async Task RecordPickupAsync(Guid userId, Guid ticketId, string action, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PickupBudget);
        var bt = budget.Token;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            string? tzId = null;
            try { tzId = await _settings.GetAsync<string>(SettingKeys.App.TimeZone, bt); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* host zone below */ }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, InsightsCalendar.ResolveTimeZone(tzId)));

            await using var conn = await _dataSource.OpenConnectionAsync(bt);
            var seen = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM workflow_pickups
                               WHERE user_id = @userId AND ticket_id = @ticketId AND pickup_date = @today)
                """,
                new { userId, ticketId, today = today.ToDateTime(TimeOnly.MinValue) }, cancellationToken: bt));
            if (seen) return;

            var views = await _rewind.ListTrackedViewsAsync(bt);
            if (views.Count == 0) return;

            var session = await _layout.BeginAsync(bt);
            foreach (var view in views)
            {
                var arranged = await session.ArrangeAsync(view, bt);
                var hit = Locate(arranged.Groups, ticketId);
                if (hit is null) continue;
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO workflow_pickups
                        (user_id, ticket_id, view_id, picked_utc, pickup_date, action,
                         group_key, group_label, position, above_ids)
                    VALUES
                        (@userId, @ticketId, @viewId, @now, @today, @action,
                         @groupKey, @groupLabel, @position, @aboveIds)
                    ON CONFLICT (user_id, ticket_id, pickup_date, view_id) DO NOTHING
                    """,
                    new
                    {
                        userId, ticketId, viewId = view.Id, now, today = today.ToDateTime(TimeOnly.MinValue), action,
                        groupKey = hit.Value.Group.Key, groupLabel = hit.Value.Group.Label,
                        position = hit.Value.Position, aboveIds = hit.Value.Above,
                    },
                    cancellationToken: bt));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes the budget running out.
            _logger.LogWarning(ex, "Workflow pickup capture skipped for ticket {TicketId}.", ticketId);
        }
    }

    /// The group holding the ticket, its 1-based position there, and the
    /// ids above it in that group (in displayed order).
    internal static (RewindArrangedGroup Group, int Position, Guid[] Above)? Locate(
        IReadOnlyList<RewindArrangedGroup> groups, Guid ticketId)
    {
        foreach (var g in groups)
        {
            for (var i = 0; i < g.Items.Count; i++)
            {
                if (g.Items[i].Id != ticketId) continue;
                return (g, i + 1, g.Items.Take(i).Select(t => t.Id).ToArray());
            }
        }
        return null;
    }
}
