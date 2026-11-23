using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Surveys;
using Servicedesk.Infrastructure.TaggingMailboxes;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Servicedesk.Infrastructure.Triggers.FirstOpenGate;
using Servicedesk.Infrastructure.Workflow;

namespace Servicedesk.Api.Tickets;

public static partial class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketRelationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // v0.0.23 ticket merge: lightweight typeahead used by the merge dialog
        // to pick a target ticket. Filters out the source ticket, soft-deleted
        // rows, and any ticket that is itself already merged. Queue-access is
        // enforced server-side: an agent cannot merge into a queue they can't
        // see, regardless of what the client sends.
        group.MapGet("/picker", async (
            string? q, Guid? excludeTicketId, int? limit, bool? recentFirst, bool? projectsOnly,
            HttpContext http, ITicketRepository repo, IQueueAccessService queueAccess,
            ISettingsService settings, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

            IReadOnlyList<Guid>? accessibleQueueIds = null;
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                accessibleQueueIds = await queueAccess.GetAccessibleQueueIdsAsync(userId, role, ct);
                if (accessibleQueueIds.Count == 0)
                    return Results.Ok(new { items = Array.Empty<object>() });
            }

            var normalizedQuery = await NormalizeTicketSearchAsync(q, settings, ct);

            // The link-parent dialog opts in to a recent-first default
            // (recentFirst=true); the merge dialog leaves it off and keeps the
            // plain global ordering. recentForUserId only changes the no-search
            // case — a typed query always searches every accessible ticket.
            var hits = await repo.SearchPickerAsync(
                search: normalizedQuery,
                excludeTicketId: excludeTicketId ?? Guid.Empty,
                accessibleQueueIds: accessibleQueueIds,
                recentForUserId: recentFirst == true ? userId : null,
                limit: limit ?? 20,
                ct: ct,
                projectsOnly: projectsOnly == true);
            return Results.Ok(new { items = hits });
        }).WithName("PickTicket");

        // v0.0.23 ticket merge endpoint. Idempotency-by-construction: if the
        // source is already merged the repository returns AlreadyMerged and we
        // 409 instead of silently re-running. Cross-customer merges require
        // explicit acknowledgement from the caller — the dialog flips the flag
        // when the requester or company differs and the agent confirms.
        group.MapPost("/{id:guid}/merge", async (
            Guid id, [FromBody] MergeTicketRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit,
            ITimesheetEntryNotifier timesheetNotifier,
            CancellationToken ct) =>
        {
            if (req.TargetTicketId == Guid.Empty)
                return Results.BadRequest(new { error = "targetTicketId is required." });
            if (req.TargetTicketId == id)
                return Results.BadRequest(new { error = "A ticket cannot be merged into itself." });

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Both tickets must be visible to the actor through queue-access.
            // Admins bypass; agents who can't see one side get a 404 so we
            // don't leak which ticket exists in a forbidden queue.
            var source = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (source is null) return Results.NotFound();

            // A target in a queue the caller can't see answers exactly like a
            // missing one, so an id probe learns nothing.
            var target = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, req.TargetTicketId, ct);
            if (target is null)
                return Results.BadRequest(new { error = "Target ticket not found." });

            var result = await tickets.MergeAsync(
                sourceTicketId: id,
                targetTicketId: req.TargetTicketId,
                actorUserId: userId,
                acknowledgedCrossCustomer: req.AcknowledgedCrossCustomer,
                ct: ct);

            if (result is null || !result.Success)
            {
                var reason = result?.FailureReason ?? MergeFailureReason.SourceNotFound;
                return reason switch
                {
                    MergeFailureReason.CrossCustomerNotAcknowledged => Results.Conflict(new
                    {
                        error = "Source and target belong to different customers or companies. " +
                                "Confirm to proceed.",
                        code = "cross_customer_unconfirmed",
                    }),
                    MergeFailureReason.AlreadyMerged => Results.Conflict(new
                    {
                        error = "One of the tickets is already merged.",
                        code = "already_merged",
                    }),
                    MergeFailureReason.WouldCycle => Results.Conflict(new
                    {
                        error = "This merge would create a cycle.",
                        code = "would_cycle",
                    }),
                    MergeFailureReason.SameTicket => Results.BadRequest(new
                    {
                        error = "A ticket cannot be merged into itself.",
                    }),
                    _ => Results.NotFound(),
                };
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.merged",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    targetTicketId = req.TargetTicketId,
                    sourceNumber = result.SourceNumber,
                    targetNumber = result.TargetNumber,
                    movedEventCount = result.MovedEventCount,
                    crossCustomer = result.CrossCustomer,
                }));

            // Both tickets need a cache-invalidation kick: the source flips to
            // Merged and gains a redirect pointer; the target gains the moved
            // events. The list is bumped once because the SignalR group
            // dedupes on the client side.
            var sourceIdStr = id.ToString();
            var targetIdStr = req.TargetTicketId.ToString();
            await hub.Clients.Group($"ticket:{sourceIdStr}").SendAsync("TicketUpdated", sourceIdStr, ct);
            await hub.Clients.Group($"ticket:{targetIdStr}").SendAsync("TicketUpdated", targetIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", targetIdStr, ct);

            // Timesheet entries were re-pointed by MergeAsync — both the
            // source panel (now empty) and the target panel (gained rows)
            // need to refetch. Without this push, viewers see stale data
            // until the 30s staleTime in the panel's useQuery expires.
            await timesheetNotifier.NotifyEntryChangedAsync(id, ct);
            await timesheetNotifier.NotifyEntryChangedAsync(req.TargetTicketId, ct);

            return Results.Ok(new
            {
                targetTicketId = req.TargetTicketId,
                sourceNumber = result.SourceNumber,
                targetNumber = result.TargetNumber,
                movedEventCount = result.MovedEventCount,
                crossCustomer = result.CrossCustomer,
            });
        }).WithName("MergeTicket");

        // Manual Main/Sub-ticket linking. Sets parent_ticket_id on the
        // current ticket (the child). Different from merge (which closes
        // the source) — the link is purely relational and both tickets
        // stay independently editable. Cycle-detection + cross-queue
        // access checks run server-side; the picker dialog reuses
        // /api/tickets/picker which already scopes to accessible queues.
        group.MapPost("/{id:guid}/link-parent", async (
            Guid id, [FromBody] LinkParentRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (req.ParentTicketId == Guid.Empty)
                return Results.BadRequest(new { error = "parentTicketId is required." });
            if (req.ParentTicketId == id)
                return Results.BadRequest(new { error = "A ticket cannot be its own parent." });

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var source = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (source is null) return Results.NotFound();

            // A parent in a queue the caller can't see answers exactly like a
            // missing one, so an id probe learns nothing.
            var parent = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, req.ParentTicketId, ct);
            if (parent is null)
                return Results.BadRequest(new { error = "Parent ticket not found." });

            var result = await tickets.LinkParentAsync(id, req.ParentTicketId, userId, ct);
            if (!result.Success)
            {
                return result.FailureReason switch
                {
                    LinkParentFailureReason.WouldCycle => Results.Conflict(new
                    {
                        error = "This link would create a cycle (the candidate parent is already a descendant of this ticket).",
                        code = "would_cycle",
                    }),
                    LinkParentFailureReason.ParentIsMerged => Results.Conflict(new
                    {
                        error = "Parent ticket is merged into another ticket and cannot be the parent of new links.",
                        code = "parent_is_merged",
                    }),
                    LinkParentFailureReason.SourceIsMerged => Results.Conflict(new
                    {
                        error = "This ticket has been merged and cannot have a parent set.",
                        code = "source_is_merged",
                    }),
                    LinkParentFailureReason.SameTicket => Results.BadRequest(new
                    {
                        error = "A ticket cannot be its own parent.",
                    }),
                    _ => Results.NotFound(),
                };
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.parent_linked",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    parentTicketId = req.ParentTicketId,
                    parentNumber = parent.Number,
                    sourceNumber = source.Number,
                }));

            // Push to both ticket SignalR groups so any open detail tab
            // refreshes its "Sub tickets" / "Main ticket" strip.
            var idStr = id.ToString();
            var parentIdStr = req.ParentTicketId.ToString();
            await hub.Clients.Group($"ticket:{idStr}").SendAsync("TicketUpdated", idStr, ct);
            await hub.Clients.Group($"ticket:{parentIdStr}").SendAsync("TicketUpdated", parentIdStr, ct);

            return Results.Ok(new
            {
                parentTicketId = req.ParentTicketId,
                parentNumber = parent.Number,
            });
        }).WithName("LinkTicketParent");

        group.MapDelete("/{id:guid}/link-parent", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var source = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (source is null) return Results.NotFound();

            var previousParentId = source.ParentTicketId;
            var ok = await tickets.UnlinkParentAsync(id, userId, ct);
            if (!ok) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.parent_unlinked",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { previousParentTicketId = previousParentId }));

            var idStr = id.ToString();
            await hub.Clients.Group($"ticket:{idStr}").SendAsync("TicketUpdated", idStr, ct);
            if (previousParentId is Guid prev)
            {
                var prevStr = prev.ToString();
                await hub.Clients.Group($"ticket:{prevStr}").SendAsync("TicketUpdated", prevStr, ct);
            }

            return Results.NoContent();
        }).WithName("UnlinkTicketParent");

        // v0.0.39 — list active manual-trigger presets visible from this
        // ticket. Each row carries the ticket-type metadata so the
        // LinkedTicketTypeDialog can render one button per row directly.
        // Returns an empty array when no active presets exist; the
        // dialog then surfaces a friendly empty-state instead of crashing.
        group.MapGet("/{id:guid}/linked-ticket-presets", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            ILinkedTicketPresetService presets, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var source = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (source is null) return Results.NotFound();
            // Merged tickets hide the picker entirely in the side panel.
            // Defensive: also return empty here so a stale UI cache can
            // never produce a usable prefill for a frozen ticket.
            if (source.MergedIntoTicketId is not null)
                return Results.Ok(Array.Empty<LinkedTicketPresetSummary>());

            var list = await presets.ListAvailablePresetsAsync(id, ct);
            return Results.Ok(list);
        }).WithName("ListLinkedTicketPresets");

        // v0.0.39 — server-rendered prefill for a single preset. The
        // drawer opens with this DTO; the agent reviews/edits and
        // submits via POST /api/tickets (which now accepts ticketTypeId
        // + initialNote). Templates are rendered against the PARENT
        // ticket's render context so #{ticket.*} resolves to the
        // parent's company / requester / queue.
        group.MapGet("/{id:guid}/linked-ticket-presets/{triggerId:guid}/prefill", async (
            Guid id, Guid triggerId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            ILinkedTicketPresetService presets, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var source = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (source is null) return Results.NotFound();
            if (source.MergedIntoTicketId is not null)
                return Results.Conflict(new { error = "Parent ticket is merged.", code = "parent_is_merged" });

            var prefill = await presets.ResolvePrefillAsync(triggerId, id, userId, ct);
            if (prefill is null) return Results.NotFound();
            return Results.Ok(prefill);
        }).WithName("GetLinkedTicketPresetPrefill");

        // v0.0.23 ticket split endpoint. Splits a multi-question mail off into
        // a fresh ticket with system defaults for queue/priority/status. The
        // source ticket gains a "Split into #X" SystemNote; the new ticket
        // gains a "Split from #Y" SystemNote and a `split_from_ticket_id`
        // pointer. Permissions: agent + queue access on source. Admins bypass.
        group.MapPost("/{id:guid}/split", async (
            Guid id, [FromBody] SplitTicketRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IMailTimelineEnricher mailEnricher,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.SourceMailEventId <= 0)
                return Results.BadRequest(new { error = "sourceMailEventId is required." });
            if (string.IsNullOrWhiteSpace(req.NewSubject))
                return Results.BadRequest(new { error = "newSubject is required." });

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var source = await tickets.GetAccessibleDetailAsync(queueAccess, userId, userRole, id, ct);
            if (source is null) return Results.NotFound();

            // Run the timeline enricher so the chosen MailReceived event's
            // `body_html` has its `cid:` references rewritten to absolute
            // /api/tickets/{sourceId}/mail/{mailId}/attachments/{attId} URLs.
            // Storing the rewritten copy on the new ticket keeps inline images
            // visible without copying the attachment rows — the agent's
            // source-queue access carries them.
            var enriched = await mailEnricher.EnrichAsync(source, ct);
            var sourceEvent = enriched.Events.FirstOrDefault(e => e.Id == req.SourceMailEventId);

            var result = await tickets.SplitAsync(
                sourceTicketId: id,
                sourceMailEventId: req.SourceMailEventId,
                newSubject: req.NewSubject.Trim(),
                actorUserId: userId,
                overrideBodyHtml: sourceEvent?.BodyHtml,
                overrideBodyText: sourceEvent?.BodyText,
                ct: ct);

            if (result is null || !result.Success)
            {
                var reason = result?.FailureReason ?? SplitFailureReason.SourceNotFound;
                return reason switch
                {
                    SplitFailureReason.SourceMerged => Results.Conflict(new
                    {
                        error = "This ticket is already merged and cannot be split.",
                        code = "source_merged",
                    }),
                    SplitFailureReason.MailEventNotFound => Results.BadRequest(new
                    {
                        error = "The selected mail does not belong to this ticket.",
                        code = "mail_event_not_found",
                    }),
                    SplitFailureReason.NotAMailEvent => Results.BadRequest(new
                    {
                        error = "Splitting is only supported on received-mail events.",
                        code = "not_a_mail_event",
                    }),
                    SplitFailureReason.DefaultsMissing => Results.Conflict(new
                    {
                        error = "Cannot split: no default queue, priority, or status is configured.",
                        code = "defaults_missing",
                    }),
                    _ => Results.NotFound(),
                };
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.split",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    newTicketId = result.NewTicketId,
                    newTicketNumber = result.NewTicketNumber,
                    sourceMailEventId = req.SourceMailEventId,
                    sourceNumber = result.SourceNumber,
                }));

            // Source ticket gains a SystemNote (so cache must invalidate);
            // the new ticket needs to surface in list views.
            var sourceIdStr = id.ToString();
            var newIdStr = result.NewTicketId!.Value.ToString();
            await hub.Clients.Group($"ticket:{sourceIdStr}").SendAsync("TicketUpdated", sourceIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", newIdStr, ct);

            return Results.Ok(new
            {
                newTicketId = result.NewTicketId,
                newTicketNumber = result.NewTicketNumber,
                sourceNumber = result.SourceNumber,
            });
        }).WithName("SplitTicket");
        return app;
    }
}
