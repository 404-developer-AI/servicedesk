using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// v0.1.54 — the <c>notify_agent</c> action: a bell notification (and live
/// toast) for agents. Payload:
/// <code>{ "kind": "notify_agent", "targets": ["form_submitter","ticket_owner"], "user_ids": ["…"], "message": "…" }</code>
/// <c>targets</c> and <c>user_ids</c> are both optional but together must
/// name at least one recipient; <c>message</c> is a plain-text template.
/// Recipients are active Agent/Admin accounts only; the person whose action
/// started the pass is never notified about their own step.
public static class NotifyAgentAction
{
    public const string Kind = "notify_agent";
    public const string NotificationType = "trigger_notice";
    public const string TargetFormSubmitter = "form_submitter";
    public const string TargetTicketOwner = "ticket_owner";
    public const int MessageMax = 500;
    public const int MaxUserIds = 50;

    public static readonly IReadOnlySet<string> Targets = new HashSet<string>(StringComparer.Ordinal)
    {
        TargetFormSubmitter, TargetTicketOwner,
    };

    public sealed record Parsed(IReadOnlyList<string> Targets, IReadOnlyList<Guid> UserIds, string Message);

    /// Validation shared by the save-time validator, the handler and the
    /// previewer. Returns an error or null.
    public static string? Validate(JsonElement action) => TryParse(action, out _, out var err) ? null : err;

    public static bool TryParse(JsonElement action, out Parsed parsed, out string? error)
    {
        parsed = new Parsed(Array.Empty<string>(), Array.Empty<Guid>(), string.Empty);
        error = null;
        var targets = new List<string>();
        if (action.TryGetProperty("targets", out var t) && t.ValueKind != JsonValueKind.Null)
        {
            if (t.ValueKind != JsonValueKind.Array) { error = "'targets' must be an array."; return false; }
            foreach (var el in t.EnumerateArray())
            {
                var v = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                if (v is null || !Targets.Contains(v)) { error = $"unknown target '{v}' (use form_submitter or ticket_owner)."; return false; }
                if (!targets.Contains(v)) targets.Add(v);
            }
        }
        var ids = new List<Guid>();
        if (action.TryGetProperty("user_ids", out var u) && u.ValueKind != JsonValueKind.Null)
        {
            if (u.ValueKind != JsonValueKind.Array) { error = "'user_ids' must be an array."; return false; }
            foreach (var el in u.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String || !Guid.TryParse(el.GetString(), out var g) || g == Guid.Empty)
                { error = "'user_ids' must contain agent ids."; return false; }
                if (!ids.Contains(g)) ids.Add(g);
            }
            if (ids.Count > MaxUserIds) { error = $"at most {MaxUserIds} agents."; return false; }
        }
        if (targets.Count == 0 && ids.Count == 0) { error = "choose at least one recipient."; return false; }

        var message = action.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? (m.GetString() ?? string.Empty).Trim() : string.Empty;
        if (message.Length == 0) { error = "requires a non-empty 'message'."; return false; }
        if (message.Length > 2000) { error = "'message' is too long."; return false; }

        parsed = new Parsed(targets, ids, message);
        return true;
    }

    /// The candidate recipients before the active-agent check.
    internal static HashSet<Guid> Candidates(Parsed p, TriggerEvaluationContext ctx)
    {
        var set = new HashSet<Guid>(p.UserIds);
        if (p.Targets.Contains(TargetFormSubmitter) && ctx.ChangeSet.FormEvent?.SubmittedByUserId is { } sub) set.Add(sub);
        if (p.Targets.Contains(TargetTicketOwner) && ctx.Ticket.AssigneeUserId is { } owner) set.Add(owner);
        // Never notify the person whose own step started this pass.
        if (ctx.ChangeSet.FormEvent?.ActorUserId is { } actor) set.Remove(actor);
        else if (ctx.TriggeringEvent?.AuthorUserId is { } author) set.Remove(author);
        return set;
    }

    internal static long? AnchorEventId(TriggerEvaluationContext ctx)
        => ctx.TriggeringEvent?.Id ?? ctx.ChangeSet.FormEvent?.AnchorEventId;

    internal static string Render(string template, TriggerEvaluationContext ctx, ITriggerTemplateRenderer renderer)
    {
        var text = ctx.RenderContext is { } rc ? renderer.Render(template, TemplateEscapeMode.PlainText, rc) : template;
        text = text.Trim();
        return text.Length > MessageMax ? text[..MessageMax] + "…" : text;
    }

    internal static async Task<IReadOnlyList<Guid>> ActiveAgentsAsync(
        NpgsqlDataSource ds, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<Guid>();
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT id FROM users
            WHERE id = ANY(@ids) AND role_name IN ('Agent', 'Admin') AND is_active = TRUE
            """, new { ids = ids.ToArray() }, cancellationToken: ct));
        return rows.ToList();
    }
}

internal sealed class NotifyAgentHandler : ITriggerActionHandler
{
    private readonly NpgsqlDataSource _ds;
    private readonly INotificationRepository _notifications;
    private readonly IUserNotifier _pusher;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly ILogger<NotifyAgentHandler> _logger;

    public NotifyAgentHandler(
        NpgsqlDataSource ds, INotificationRepository notifications, IUserNotifier pusher,
        ITriggerTemplateRenderer renderer, ILogger<NotifyAgentHandler> logger)
    {
        _ds = ds;
        _notifications = notifications;
        _pusher = pusher;
        _renderer = renderer;
        _logger = logger;
    }

    public string Kind => NotifyAgentAction.Kind;

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!NotifyAgentAction.TryParse(actionJson, out var p, out var err))
            return TriggerActionResult.Failed(Kind, err ?? "Invalid action.");
        if (NotifyAgentAction.AnchorEventId(ctx) is not { } eventId)
            return TriggerActionResult.NoOp(Kind, new { reason = "no event to link the notification to" });

        var recipients = await NotifyAgentAction.ActiveAgentsAsync(_ds, NotifyAgentAction.Candidates(p, ctx), ct);
        if (recipients.Count == 0)
            return TriggerActionResult.NoOp(Kind, new { reason = "no recipients" });

        var message = NotifyAgentAction.Render(p.Message, ctx, _renderer);
        var rows = await _notifications.CreateManyAsync(recipients.Select(r => new NewUserNotification(
            UserId: r,
            SourceUserId: null,
            NotificationType: NotifyAgentAction.NotificationType,
            TicketId: ctx.TicketId,
            TicketNumber: ctx.Ticket.Number,
            TicketSubject: ctx.Ticket.Subject,
            EventId: eventId,
            EventType: "TriggerNotice",
            PreviewText: message)).ToList(), ct);

        foreach (var row in rows)
        {
            try
            {
                await _pusher.NotifyMentionAsync(row.UserId, new UserNotificationPush(
                    row.Id, row.TicketId, row.TicketNumber, row.TicketSubject, null,
                    row.EventId, row.EventType, row.PreviewText, row.CreatedUtc,
                    NotificationType: NotifyAgentAction.NotificationType), ct);
            }
            catch (Exception ex)
            {
                // The bell row is written; a failed live push only costs the toast.
                _logger.LogWarning(ex, "notify_agent push failed for user {UserId}.", row.UserId);
            }
        }
        return TriggerActionResult.Applied(Kind, new { recipients = rows.Count });
    }
}

internal sealed class NotifyAgentPreviewer : ITriggerActionPreviewer
{
    private readonly NpgsqlDataSource _ds;
    private readonly ITriggerTemplateRenderer _renderer;

    public NotifyAgentPreviewer(NpgsqlDataSource ds, ITriggerTemplateRenderer renderer)
    {
        _ds = ds;
        _renderer = renderer;
    }

    public string Kind => NotifyAgentAction.Kind;

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!NotifyAgentAction.TryParse(actionJson, out var p, out var err))
            return TriggerActionPreviewResult.Failed(Kind, err ?? "Invalid action.");
        if (NotifyAgentAction.AnchorEventId(ctx) is null)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = "no event to link the notification to" });
        var recipients = await NotifyAgentAction.ActiveAgentsAsync(_ds, NotifyAgentAction.Candidates(p, ctx), ct);
        if (recipients.Count == 0)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = "no recipients" });
        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            recipients = recipients.Count,
            message = NotifyAgentAction.Render(p.Message, ctx, _renderer),
        });
    }
}
