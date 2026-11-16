using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.TagGroups;

namespace Servicedesk.Api.TagGroups;

/// v0.1.54 — group tags. Groups of agents (admin config on Settings → Users)
/// that can be @@-tagged like a person; a group mention writes one shared
/// tag that every member sees in the sidebar until one of them acknowledges
/// it — or, for a personal-acknowledgement group (e.g. "Everyone"), until
/// each member marks it as read for themselves. Never a mail.
/// <list type="bullet">
/// <item><b>Admin</b> — CRUD under <c>/api/settings/tag-groups</c>.</item>
/// <item><b>Agent</b> — picker search <c>/api/tag-groups/search</c>; the
/// caller's own tags under <c>/api/group-tags</c> (only groups they belong to).</item>
/// </list>
public static class TagGroupEndpoints
{
    public const int MaxNameLength = 200;
    public const int MaxMembers = 500;

    public static IEndpointRouteBuilder MapTagGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/settings/tag-groups")
            .WithTags("TagGroups")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/", async (ITagGroupRepository repo, CancellationToken ct) =>
            Results.Ok((await repo.ListAsync(ct)).Select(MapGroup))).WithName("ListTagGroups");

        admin.MapPost("/", async (
            [FromBody] UpsertRequest req, HttpContext http, ITagGroupRepository repo,
            IUserService users, IAuditLogger audit, CancellationToken ct) =>
        {
            var (err, name, members) = await ValidateAsync(req, users, ct);
            if (err is not null) return Results.BadRequest(new { error = err });
            Guid id;
            try { id = await repo.CreateAsync(name!, req.IsActive ?? true, req.PersonalAck ?? false, members!, ct); }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
            {
                return Results.Conflict(new { error = "An active group with this name already exists." });
            }
            await AuditAsync(audit, http, "tag_group.create", id,
                new { name, personalAck = req.PersonalAck ?? false, memberCount = members!.Count });
            return Results.Created($"/api/settings/tag-groups/{id}", MapGroup((await repo.GetAsync(id, ct))!));
        }).WithName("CreateTagGroup");

        admin.MapPut("/{id:guid}", async (
            Guid id, [FromBody] UpsertRequest req, HttpContext http, ITagGroupRepository repo,
            IUserService users, IAuditLogger audit, CancellationToken ct) =>
        {
            var (err, name, members) = await ValidateAsync(req, users, ct);
            if (err is not null) return Results.BadRequest(new { error = err });
            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            try
            {
                if (!await repo.UpdateAsync(id, name!, req.IsActive ?? existing.IsActive,
                        req.PersonalAck ?? existing.PersonalAck, members!, ct))
                    return Results.NotFound();
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
            {
                return Results.Conflict(new { error = "Another active group already uses this name." });
            }
            await AuditAsync(audit, http, "tag_group.update", id,
                new
                {
                    name, isActive = req.IsActive ?? existing.IsActive,
                    personalAck = req.PersonalAck ?? existing.PersonalAck, memberCount = members!.Count,
                });
            return Results.Ok(MapGroup((await repo.GetAsync(id, ct))!));
        }).WithName("UpdateTagGroup");

        admin.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ITagGroupRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            // Members and the group's tags (incl. history) go with it.
            await repo.DeleteAsync(id, ct);
            await AuditAsync(audit, http, "tag_group.delete", id, new { existing.Name });
            return Results.NoContent();
        }).WithName("DeleteTagGroup");

        // @@-picker — same posture as /api/tagging-mailboxes/search.
        app.MapGet("/api/tag-groups/search", async (string? q, int? limit, ITagGroupRepository repo, CancellationToken ct) =>
        {
            var rows = await repo.SearchActiveAsync(q, limit ?? 20, ct);
            return Results.Ok(rows.Select(r => new { id = r.Id, name = r.Name, memberCount = r.MemberCount, personalAck = r.PersonalAck }));
        })
            .WithTags("TagGroupsAgent")
            .WithName("SearchTagGroups")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        var tags = app.MapGroup("/api/group-tags")
            .WithTags("GroupTags")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        tags.MapGet("/pending", async (HttpContext http, IGroupTagRepository repo, ITagGroupRepository groups, CancellationToken ct) =>
        {
            var userId = CurrentUserId(http);
            var items = await repo.ListPendingForUserAsync(userId, ct);
            return Results.Ok(new
            {
                isMember = items.Count > 0 || await groups.IsMemberOfAnyActiveAsync(userId, ct),
                items = items.Select(MapTag),
            });
        }).WithName("ListPendingGroupTags");

        tags.MapGet("/history", async (
            [FromQuery] DateTime? cursorUtc, [FromQuery] Guid? cursorId, [FromQuery] int? limit,
            HttpContext http, IGroupTagRepository repo, CancellationToken ct) =>
        {
            var effective = Math.Clamp(limit ?? 50, 1, 200);
            var cursor = cursorUtc is { } c && cursorId is { } i ? new GroupTagCursor(DateTime.SpecifyKind(c, DateTimeKind.Utc), i) : null;
            var rows = await repo.ListHistoryForUserAsync(CurrentUserId(http), cursor, effective, ct);
            var last = rows.Count == effective ? rows[^1] : null;
            return Results.Ok(new
            {
                items = rows.Select(MapTag),
                nextCursor = last is null ? null : new { createdUtc = last.CreatedUtc, id = last.Id },
            });
        }).WithName("ListGroupTagHistory");

        tags.MapPost("/{id:guid}/ack", async (
            Guid id, HttpContext http, IGroupTagRepository repo, ITagGroupRepository groups,
            IGroupTagNotifier notifier, IAuditLogger audit, ILoggerFactory logs, CancellationToken ct) =>
        {
            var userId = CurrentUserId(http);
            var result = await repo.AckAsync(id, userId, ct);
            if (result is null)
            {
                // Someone else was quicker (or it is gone): the caller only
                // needs a refresh. Not a member → 404, like any other id probe.
                return await repo.IsVisibleToAsync(id, userId, ct)
                    ? Results.Ok(new { acked = false })
                    : Results.NotFound();
            }
            var groupId = result.GroupId;
            // Personal tag: only the caller read it — refresh just their own
            // tabs. Shared tag: gone for the whole group.
            await AuditAsync(audit, http, result.Personal ? "group_tag.read" : "group_tag.acked", id, new { groupId });
            try
            {
                if (result.Personal)
                {
                    await notifier.GroupTagsChangedAsync(new[] { userId }, ct);
                }
                else
                {
                    var members = await groups.ListMemberIdsAsync(new[] { groupId }, ct);
                    await notifier.GroupTagsChangedAsync(members.GetValueOrDefault(groupId) ?? Array.Empty<Guid>(), ct);
                }
            }
            catch (Exception ex)
            {
                logs.CreateLogger("GroupTags").LogWarning(ex, "Group tag refresh push failed for tag {TagId}.", id);
            }
            return Results.Ok(new { acked = true });
        }).WithName("AckGroupTag");

        return app;
    }

    /// <c>PersonalAck</c> — every member acknowledges each tag personally
    /// (null on update = keep). Only affects tags made after the change.
    public sealed record UpsertRequest(string? Name, bool? IsActive, IReadOnlyList<Guid>? MemberIds, bool? PersonalAck = null);

    private static async Task<(string? Error, string? Name, IReadOnlyList<Guid>? Members)> ValidateAsync(
        UpsertRequest req, IUserService users, CancellationToken ct)
    {
        if (req is null) return ("Body is required.", null, null);
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameLength)
            return ($"Name is required and must be at most {MaxNameLength} characters.", null, null);
        var ids = (req.MemberIds ?? Array.Empty<Guid>()).Where(g => g != Guid.Empty).Distinct().ToList();
        if (ids.Count > MaxMembers) return ($"A group can have at most {MaxMembers} members.", null, null);
        // Members are agents/admins only — customers and unknown ids are refused.
        var agents = await users.FilterAgentIdsAsync(ids, ct);
        if (agents.Count != ids.Count) return ("Only agents can be members of a group.", null, null);
        return (null, name, ids);
    }

    private static Guid CurrentUserId(HttpContext http)
        => Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    private static object MapGroup(TagGroup g) => new
    {
        id = g.Id,
        name = g.Name,
        isActive = g.IsActive,
        personalAck = g.PersonalAck,
        members = g.Members.Select(m => new { userId = m.UserId, email = m.Email }),
        createdUtc = g.CreatedUtc,
        updatedUtc = g.UpdatedUtc,
    };

    internal static object MapTag(GroupTagRow t) => new
    {
        id = t.Id,
        groupId = t.GroupId,
        groupName = t.GroupName,
        ticketId = t.TicketId,
        ticketNumber = t.TicketNumber,
        ticketSubject = t.TicketSubject,
        eventId = t.EventId,
        eventType = t.EventType,
        sourceUserEmail = t.SourceUserEmail,
        previewText = t.PreviewText,
        createdUtc = t.CreatedUtc,
        ackedUtc = t.AckedUtc,
        ackedByEmail = t.AckedByEmail,
        personalAck = t.PersonalAck,
        readByMeUtc = t.ReadByMeUtc,
        readCount = t.ReadCount,
        recipientCount = t.RecipientCount,
    };

    private static Task AuditAsync(IAuditLogger audit, HttpContext http, string eventType, Guid target, object? payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }
}
