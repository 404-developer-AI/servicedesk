using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// Agent-facing endpoints for the Timesheet → Comments feature (v0.0.84).
/// A reviewer on the Adsolut / Resolved / CWI tabs opens a per-ticket chat
/// thread and links one or more timesheet colleagues; the linked users see the
/// thread in their own Comments inbox and can reply (linking is mandatory both
/// ways). All routes carry RequireAgent — the data is install-wide back-office
/// review chatter, like the rest of the timesheet tabs; the per-user Comments
/// tab visibility is a client-side concern (everyone with timesheet active).
public static class TimesheetCommentEndpoints
{
    public static IEndpointRouteBuilder MapTimesheetCommentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/comments")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/inbox", GetInbox).WithName("ListTimesheetCommentInbox").WithOpenApi();
        group.MapGet("/unread-count", GetUnreadCount).WithName("GetTimesheetCommentUnreadCount").WithOpenApi();
        group.MapGet("/recipients", GetRecipients).WithName("ListTimesheetCommentRecipients").WithOpenApi();
        group.MapGet("/threads/{threadId:guid}", GetThread).WithName("GetTimesheetCommentThread").WithOpenApi();
        group.MapPost("/threads/{threadId:guid}/read", MarkRead).WithName("MarkTimesheetCommentThreadRead").WithOpenApi();
        group.MapPost("/messages", PostMessage).WithName("PostTimesheetComment").WithOpenApi();

        return group;
    }

    private static async Task<IResult> GetInbox(
        HttpContext http, ITimesheetCommentService svc, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        var rows = await svc.GetInboxAsync(userId, ct);
        return Results.Ok(rows.Select(r => new
        {
            threadId = r.ThreadId,
            ticketNumber = r.TicketNumber,
            ticketId = r.TicketId,
            originContext = r.OriginContext,
            lastMessageUtc = r.LastMessageUtc,
            lastMessagePreview = r.LastMessagePreview,
            lastAuthorEmail = r.LastAuthorEmail,
            unreadCount = r.UnreadCount,
        }));
    }

    private static async Task<IResult> GetUnreadCount(
        HttpContext http, ITimesheetCommentService svc, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        var count = await svc.GetUnreadThreadCountAsync(userId, ct);
        return Results.Ok(new { count });
    }

    private static async Task<IResult> GetRecipients(
        HttpContext http, ITimesheetCommentService svc, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        var users = await svc.GetRecipientOptionsAsync(userId, ct);
        return Results.Ok(users.Select(u => new { id = u.Id, email = u.Email }));
    }

    private static async Task<IResult> GetThread(
        Guid threadId, ITimesheetCommentService svc, CancellationToken ct)
    {
        var detail = await svc.GetThreadAsync(threadId, ct);
        if (detail is null) return Results.NotFound(new { error = "not_found" });

        var byMessage = detail.Recipients
            .GroupBy(r => r.CommentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return Results.Ok(new
        {
            id = detail.Id,
            ticketNumber = detail.TicketNumber,
            ticketId = detail.TicketId,
            originContext = detail.OriginContext,
            createdBy = detail.CreatedBy,
            createdByEmail = detail.CreatedByEmail,
            messages = detail.Messages.Select(m => new
            {
                id = m.Id,
                authorId = m.AuthorId,
                authorEmail = m.AuthorEmail,
                body = m.Body,
                createdUtc = m.CreatedUtc,
                recipients = (byMessage.TryGetValue(m.Id, out var rs) ? rs : new List<CommentRecipientRow>())
                    .Select(r => new { userId = r.UserId, email = r.Email }),
            }),
        });
    }

    private static async Task<IResult> MarkRead(
        Guid threadId, HttpContext http, ITimesheetCommentService svc, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        await svc.MarkThreadReadAsync(threadId, userId, ct);
        return Results.Ok(new { ok = true });
    }

    public sealed record PostMessageRequest(
        Guid? ThreadId,
        long TicketNumber,
        Guid? TicketId,
        string? Context,
        Guid? EntityId,
        string? Body,
        IReadOnlyList<Guid>? RecipientIds);

    private static async Task<IResult> PostMessage(
        PostMessageRequest req,
        HttpContext http,
        ITimesheetCommentService svc,
        ITimesheetCommentNotifier notifier,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "invalid_request" });

        var (actor, _) = ActorContext.Resolve(http);
        var userId = ActorContext.GetUserId(http);

        var result = await svc.PostMessageAsync(new PostCommentInput(
            req.ThreadId,
            req.TicketNumber,
            req.TicketId,
            req.Context,
            req.EntityId,
            req.Body ?? string.Empty,
            req.RecipientIds ?? Array.Empty<Guid>()), userId, ct);

        if (!result.Ok)
        {
            return result.Error switch
            {
                "thread_not_found" => Results.NotFound(new { error = result.Error }),
                _ => Results.BadRequest(new { error = result.Error }),
            };
        }

        // Push a per-user signal to each freshly-linked recipient so their
        // open session refreshes its inbox + red dots. Best-effort: a failed
        // push never fails the post (the data is already committed).
        var payload = new TimesheetCommentPush(result.ThreadId, result.TicketNumber, result.OriginContext, actor);
        try
        {
            await Task.WhenAll(result.NotifyUserIds.Select(uid => notifier.NotifyCommentAsync(uid, payload, ct)));
        }
        catch
        {
            // swallow — delivery is best-effort
        }

        return Results.Ok(new { ok = true, threadId = result.ThreadId, messageId = result.MessageId });
    }
}
