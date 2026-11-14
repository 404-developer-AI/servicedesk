using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Tickets.NoteThreads;

namespace Servicedesk.Api.Tickets;

/// v0.1.54 — replies and likes on internal notes / internal calls. Staff only
/// (RequireAgent) and always under the ticket's queue access via the shared
/// mutation precheck inside <see cref="INoteThreadService"/>; a note, reply
/// or like id is always resolved against the ticket in the route. The portal
/// never reaches these tables.
public static class NoteThreadEndpoints
{
    public static IEndpointRouteBuilder MapNoteThreadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets/{ticketId:guid}")
            .WithTags("NoteThreads")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/note-threads", async (Guid ticketId, HttpContext http, INoteThreadService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var data = await svc.GetAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, ct);
                return Results.Ok(new
                {
                    replies = data.Replies.Select(MapReply),
                    likes = data.Likes.Select(l => new { eventId = l.EventId, replyId = l.ReplyId, userId = l.UserId, email = l.Email }),
                });
            })).WithName("GetNoteThreads").WithOpenApi();

        group.MapPost("/events/{eventId:long}/replies", async (
            Guid ticketId, long eventId, [FromBody] ReplyRequest req, HttpContext http, INoteThreadService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                var reply = await svc.AddReplyAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, eventId,
                    new NoteReplyInput(req?.ParentReplyId, req?.BodyHtml, req?.MentionedUserIds, req?.MentionedGroupIds), ct);
                return Results.Created($"/api/tickets/{ticketId}/replies/{reply.Id}", MapReply(reply));
            })).WithName("AddNoteReply").WithOpenApi();

        group.MapPut("/replies/{replyId:long}", async (
            Guid ticketId, long replyId, [FromBody] EditRequest req, HttpContext http, INoteThreadService svc, CancellationToken ct) =>
            await Run(async () => Results.Ok(MapReply(
                await svc.EditReplyAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, replyId, req?.BodyHtml, ct))))
        ).WithName("EditNoteReply").WithOpenApi();

        group.MapDelete("/replies/{replyId:long}", async (
            Guid ticketId, long replyId, HttpContext http, INoteThreadService svc, CancellationToken ct) =>
            await Run(async () =>
            {
                await svc.DeleteReplyAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, replyId, ct);
                return Results.NoContent();
            })).WithName("DeleteNoteReply").WithOpenApi();

        group.MapPut("/events/{eventId:long}/like", (Guid ticketId, long eventId, HttpContext http, INoteThreadService svc, CancellationToken ct)
            => Like(svc, http, ticketId, eventId, null, true, ct)).WithName("LikeNote").WithOpenApi();
        group.MapDelete("/events/{eventId:long}/like", (Guid ticketId, long eventId, HttpContext http, INoteThreadService svc, CancellationToken ct)
            => Like(svc, http, ticketId, eventId, null, false, ct)).WithName("UnlikeNote").WithOpenApi();
        group.MapPut("/replies/{replyId:long}/like", (Guid ticketId, long replyId, HttpContext http, INoteThreadService svc, CancellationToken ct)
            => Like(svc, http, ticketId, null, replyId, true, ct)).WithName("LikeNoteReply").WithOpenApi();
        group.MapDelete("/replies/{replyId:long}/like", (Guid ticketId, long replyId, HttpContext http, INoteThreadService svc, CancellationToken ct)
            => Like(svc, http, ticketId, null, replyId, false, ct)).WithName("UnlikeNoteReply").WithOpenApi();

        return app;
    }

    private static Task<IResult> Like(INoteThreadService svc, HttpContext http, Guid ticketId, long? eventId, long? replyId, bool liked, CancellationToken ct)
        => Run(async () =>
        {
            await svc.SetLikeAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, eventId, replyId, liked, ct);
            return Results.NoContent();
        });

    private static async Task<IResult> Run(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (NoteThreadRejectedException ex)
        {
            var status = ex.Code switch
            {
                NoteThreadRejectCode.NotFound => 404,
                NoteThreadRejectCode.Forbidden => 403,
                _ => 400,
            };
            return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: status);
        }
    }

    private static object MapReply(NoteReply r) => new
    {
        id = r.Id,
        rootEventId = r.RootEventId,
        parentReplyId = r.ParentReplyId,
        depth = r.Depth,
        authorUserId = r.AuthorUserId,
        authorEmail = r.AuthorEmail,
        bodyHtml = r.DeletedUtc is null ? r.BodyHtml : string.Empty,
        createdUtc = r.CreatedUtc,
        editedUtc = r.EditedUtc,
        deleted = r.DeletedUtc is not null,
    };

    public sealed record ReplyRequest(long? ParentReplyId, string? BodyHtml, IReadOnlyList<Guid>? MentionedUserIds, IReadOnlyList<Guid>? MentionedGroupIds);
    public sealed record EditRequest(string? BodyHtml);
}
