using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Tickets;

/// v0.0.104 — project tickets. Convert/revert on the project side,
/// link/unlink/reorder on the member side, the panel overview and the
/// first-open link prompt. Everything is agent-gated + queue-access
/// checked like the rest of the ticket surface, and every mutation is
/// audited and pushed over SignalR. The Projects.Enabled setting is
/// re-enforced server-side on every mutation so a stale client cannot
/// keep using a switched-off feature.
public static class TicketProjectEndpoints
{
    public static IEndpointRouteBuilder MapTicketProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("TicketProjects")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // Convert an existing ticket into a project ticket.
        group.MapPost("/{id:guid}/project/convert", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, ISettingsService settings,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await ProjectsEnabledAsync(settings, ct))
                return ProjectsDisabled();
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;

            // v0.0.104 — pinned project queue: converting moves the ticket
            // there, so the actor also needs access to that queue.
            var pinQueueId = await ProjectQueuePin.GetPinnedQueueIdAsync(settings, ct);
            if (pinQueueId is Guid pin
                && !await queueAccess.HasQueueAccessAsync(access.UserId, access.Role, pin, ct))
            {
                return Results.Json(
                    new { error = "Converting moves the ticket to the project queue, which you do not have access to.", code = "queue_forbidden" },
                    statusCode: 403);
            }

            var result = await projects.ConvertToProjectAsync(id, access.UserId, pinQueueId, ct);
            if (!result.Success) return MapFailure(result.FailureReason);

            await AuditAsync(audit, http, "ticket.project_converted", id,
                new { movedToQueueId = pinQueueId });
            await PushTicketAsync(hub, id, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", id.ToString(), ct);
            return Results.Ok(new { isProject = true });
        }).WithName("ConvertTicketToProject").WithOpenApi();

        // Turn a project ticket back into a normal ticket (only while no
        // tickets are linked to it).
        group.MapPost("/{id:guid}/project/revert", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, ISettingsService settings,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await ProjectsEnabledAsync(settings, ct))
                return ProjectsDisabled();
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;

            var result = await projects.RevertProjectAsync(id, access.UserId, ct);
            if (!result.Success) return MapFailure(result.FailureReason);

            await AuditAsync(audit, http, "ticket.project_reverted", id, new { });
            await PushTicketAsync(hub, id, ct);
            return Results.Ok(new { isProject = false });
        }).WithName("RevertTicketProject").WithOpenApi();

        // Link this (normal) ticket to a project ticket. Both sides need
        // queue access; 404 on the target so a forbidden project's
        // existence is not leaked (same posture as merge/link-parent).
        group.MapPost("/{id:guid}/project/link", async (
            Guid id, [FromBody] LinkProjectRequest req, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, ISettingsService settings,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.ProjectTicketId == Guid.Empty)
                return Results.BadRequest(new { error = "projectTicketId is required." });
            if (!await ProjectsEnabledAsync(settings, ct))
                return ProjectsDisabled();
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;

            var target = await tickets.GetByIdAsync(req.ProjectTicketId, ct);
            if (target is null)
                return Results.BadRequest(new { error = "Project ticket not found." });
            if (!await queueAccess.HasQueueAccessAsync(access.UserId, access.Role, target.Ticket.QueueId, ct))
                return Results.Json(
                    new { error = "You do not have access to the project ticket's queue.", code = "queue_forbidden" },
                    statusCode: 403);

            var result = await projects.LinkToProjectAsync(id, req.ProjectTicketId, access.UserId, ct);
            if (!result.Success) return MapFailure(result.FailureReason);

            await AuditAsync(audit, http, "ticket.project_linked", id, new
            {
                projectTicketId = req.ProjectTicketId,
                projectNumber = result.ProjectNumber,
                previousProjectTicketId = result.PreviousProjectTicketId,
            });
            await PushTicketAsync(hub, id, ct);
            await PushTicketAsync(hub, req.ProjectTicketId, ct);
            if (result.PreviousProjectTicketId is Guid prev && prev != req.ProjectTicketId)
                await PushTicketAsync(hub, prev, ct);
            return Results.Ok(new { projectTicketId = req.ProjectTicketId, projectNumber = result.ProjectNumber });
        }).WithName("LinkTicketToProject").WithOpenApi();

        group.MapDelete("/{id:guid}/project/link", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, ISettingsService settings,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await ProjectsEnabledAsync(settings, ct))
                return ProjectsDisabled();
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;

            var result = await projects.UnlinkFromProjectAsync(id, access.UserId, ct);
            if (!result.Success) return MapFailure(result.FailureReason);

            await AuditAsync(audit, http, "ticket.project_unlinked", id,
                new { previousProjectTicketId = result.PreviousProjectTicketId });
            await PushTicketAsync(hub, id, ct);
            if (result.PreviousProjectTicketId is Guid prev)
                await PushTicketAsync(hub, prev, ct);
            return Results.NoContent();
        }).WithName("UnlinkTicketFromProject").WithOpenApi();

        // Manual priority order inside the project panel (drag & drop).
        group.MapPost("/{id:guid}/project/reorder", async (
            Guid id, [FromBody] ReorderProjectRequest req, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, ISettingsService settings,
            IHubContext<TicketPresenceHub> hub, CancellationToken ct) =>
        {
            if (req.OrderedTicketIds is null || req.OrderedTicketIds.Count == 0)
                return Results.BadRequest(new { error = "orderedTicketIds is required." });
            if (req.OrderedTicketIds.Count > 500)
                return Results.BadRequest(new { error = "Too many ids in one reorder." });
            if (!await ProjectsEnabledAsync(settings, ct))
                return ProjectsDisabled();
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;
            if (!access.Ticket!.Ticket.IsProject)
                return Results.Conflict(new { error = "This ticket is not a project.", code = "not_a_project" });

            await projects.ReorderAsync(id, req.OrderedTicketIds, ct);
            await PushTicketAsync(hub, id, ct);
            return Results.NoContent();
        }).WithName("ReorderProjectTickets").WithOpenApi();

        // The project panel: linked tickets + per-ticket/per-task time
        // rollup. Non-admins only see linked tickets in queues they can
        // access; hiddenTicketCount tells the panel how many were withheld.
        group.MapGet("/{id:guid}/project/overview", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, CancellationToken ct) =>
        {
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;

            IReadOnlyList<Guid>? accessibleQueueIds = null;
            if (!string.Equals(access.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                accessibleQueueIds = await queueAccess.GetAccessibleQueueIdsAsync(access.UserId, access.Role, ct);

            var overview = await projects.GetOverviewAsync(id, accessibleQueueIds, ct);
            if (overview is null)
                return Results.Conflict(new { error = "This ticket is not a project.", code = "not_a_project" });

            return Results.Ok(new
            {
                tickets = overview.Tickets,
                timeRows = overview.TimeRows,
                hiddenTicketCount = overview.HiddenTicketCount,
            });
        }).WithName("GetProjectOverview").WithOpenApi();

        // First-open link prompt probe: open projects on this ticket's
        // company, empty when the prompt does not apply. The detail page
        // calls this once on load, like the other first-open gates.
        group.MapGet("/{id:guid}/project/prompt", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, ISettingsService settings, CancellationToken ct) =>
        {
            if (!await ProjectsEnabledAsync(settings, ct) ||
                !await LinkPromptEnabledAsync(settings, ct))
                return Results.Ok(new { projects = Array.Empty<object>() });
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;

            var candidates = await projects.GetPromptCandidatesAsync(id, ct);
            // Only offer projects whose queue the agent can access — the
            // link call would 403 on them anyway.
            if (!string.Equals(access.Role, "Admin", StringComparison.OrdinalIgnoreCase) && candidates.Count > 0)
            {
                var accessible = await queueAccess.GetAccessibleQueueIdsAsync(access.UserId, access.Role, ct);
                candidates = candidates.Where(c => accessible.Contains(c.QueueId)).ToList();
            }
            return Results.Ok(new
            {
                projects = candidates.Select(c => new { id = c.Id, number = c.Number.ToString(), subject = c.Subject }),
            });
        }).WithName("GetProjectLinkPrompt").WithOpenApi();

        // "No" on the prompt — remembered per ticket so it never re-asks.
        group.MapPost("/{id:guid}/project/prompt/dismiss", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, ITicketProjectRepository projects,
            IQueueAccessService queueAccess, CancellationToken ct) =>
        {
            var access = await CheckAccessAsync(id, http, tickets, queueAccess, ct);
            if (access.Error is not null) return access.Error;

            await projects.DismissPromptAsync(id, ct);
            return Results.NoContent();
        }).WithName("DismissProjectLinkPrompt").WithOpenApi();

        return app;
    }

    private sealed record AccessCheck(Guid UserId, string Role, TicketDetail? Ticket, IResult? Error);

    /// Shared preamble: resolve actor, load the ticket, enforce queue
    /// access (404 when hidden, so existence is not leaked).
    private static async Task<AccessCheck> CheckAccessAsync(
        Guid ticketId, HttpContext http, ITicketRepository tickets,
        IQueueAccessService queueAccess, CancellationToken ct)
    {
        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

        var detail = await tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return new AccessCheck(userId, role, null, Results.NotFound());
        if (!await queueAccess.HasQueueAccessAsync(userId, role, detail.Ticket.QueueId, ct))
            return new AccessCheck(userId, role, null, Results.NotFound());
        return new AccessCheck(userId, role, detail, null);
    }

    private static async Task<bool> ProjectsEnabledAsync(ISettingsService settings, CancellationToken ct)
    {
        try { return await settings.GetAsync<bool>(SettingKeys.Projects.Enabled, ct); }
        catch { return true; }
    }

    private static async Task<bool> LinkPromptEnabledAsync(ISettingsService settings, CancellationToken ct)
    {
        try { return await settings.GetAsync<bool>(SettingKeys.Projects.LinkPromptEnabled, ct); }
        catch { return true; }
    }

    private static IResult ProjectsDisabled() =>
        Results.Conflict(new { error = "Project tickets are disabled.", code = "projects_disabled" });

    private static IResult MapFailure(ProjectFailureReason? reason) => reason switch
    {
        ProjectFailureReason.NotFound or ProjectFailureReason.TargetNotFound => Results.NotFound(),
        ProjectFailureReason.IsMerged => Results.Conflict(new
        {
            error = "This ticket has been merged and cannot take part in a project.",
            code = "is_merged",
        }),
        ProjectFailureReason.AlreadyProject => Results.Conflict(new
        {
            error = "This ticket is already a project.",
            code = "already_project",
        }),
        ProjectFailureReason.NotAProject => Results.Conflict(new
        {
            error = "This ticket is not a project.",
            code = "not_a_project",
        }),
        ProjectFailureReason.LinkedToProject => Results.Conflict(new
        {
            error = "This ticket is linked to a project. Unlink it before converting it to a project.",
            code = "linked_to_project",
        }),
        ProjectFailureReason.HasLinkedTickets => Results.Conflict(new
        {
            error = "Tickets are still linked to this project. Unlink them before converting it back to a normal ticket.",
            code = "has_linked_tickets",
        }),
        ProjectFailureReason.TargetNotProject => Results.Conflict(new
        {
            error = "The selected ticket is not a project.",
            code = "target_not_project",
        }),
        ProjectFailureReason.TargetIsMerged => Results.Conflict(new
        {
            error = "The selected project ticket is merged.",
            code = "target_is_merged",
        }),
        ProjectFailureReason.SourceIsProject => Results.Conflict(new
        {
            error = "A project ticket cannot be linked to another project.",
            code = "source_is_project",
        }),
        ProjectFailureReason.SameTicket => Results.BadRequest(new
        {
            error = "A ticket cannot be linked to itself.",
        }),
        ProjectFailureReason.NotLinked => Results.NotFound(),
        _ => Results.NotFound(),
    };

    private static async Task AuditAsync(
        IAuditLogger audit, HttpContext http, string eventType, Guid ticketId, object payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: ticketId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }

    private static async Task PushTicketAsync(
        IHubContext<TicketPresenceHub> hub, Guid ticketId, CancellationToken ct)
    {
        var idStr = ticketId.ToString();
        await hub.Clients.Group($"ticket:{idStr}").SendAsync("TicketUpdated", idStr, ct);
    }

    public sealed record LinkProjectRequest(Guid ProjectTicketId);

    public sealed record ReorderProjectRequest(IReadOnlyList<Guid> OrderedTicketIds);
}
