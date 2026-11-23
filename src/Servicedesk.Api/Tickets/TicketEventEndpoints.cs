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
    public static IEndpointRouteBuilder MapTicketEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapPost("/{id:guid}/events", async (
            Guid id, [FromBody] AddEventRequest req, HttpContext http,
            ITicketMutationService mutations,
            IAttachmentRepository attachmentsRepo, IUserService users,
            ITaggingMailboxRepository taggingMailboxes,
            IMentionNotificationService mentionService,
            IComposeTemplateSurveyDispatcher surveyTemplateDispatcher,
            ISettingsService settings,
            IWorkflowCaptureService workflow,
            Servicedesk.Infrastructure.Tickets.ITicketHtmlSanitizer htmlSanitizer,
            CancellationToken ct) =>
        {
            req = req with { BodyHtml = htmlSanitizer.Sanitize(req.BodyHtml) };
            if (string.IsNullOrWhiteSpace(req.EventType))
                return Results.BadRequest(new { error = "eventType is required." });
            if (req.EventType != "Comment" && req.EventType != "Note" && req.EventType != "Call")
                return Results.BadRequest(new { error = "eventType must be 'Comment', 'Note' or 'Call'." });

            var userId = ActorContext.GetUserId(http);
            var actor = ResolveMutationActor(http);

            // Queue-access check via parent ticket (shared rule, v0.0.102)
            var access = await mutations.PrecheckAccessAsync(actor, id, ct);
            if (access.Check != TicketMutationCheck.Ok || access.Ticket is null) return Results.NotFound();
            var ticket = access.Ticket;

            // @@-mention filtering (v0.0.12 stap 3): unknown ids, customer ids,
            // or deleted-user ids are silently dropped. Same soft-drop semantics
            // as the attachment-ownership guard — a stale draft mentioning an
            // ex-agent shouldn't fail the submit.
            IReadOnlyList<Guid> mentionedIds = Array.Empty<Guid>();
            if (req.MentionedUserIds is { Count: > 0 } incomingMentions)
            {
                mentionedIds = await users.FilterAgentIdsAsync(incomingMentions, ct);
            }
            // Tagging-only mailboxes are a parallel mention target: filtered to
            // active rows the same soft-drop way agent ids are.
            IReadOnlyList<Guid> mentionedMailboxIds = Array.Empty<Guid>();
            if (req.MentionedMailboxIds is { Count: > 0 } incomingMailboxes)
            {
                var resolved = await taggingMailboxes.ResolveActiveByIdsAsync(incomingMailboxes, ct);
                mentionedMailboxIds = resolved.Select(m => m.Id).ToList();
            }
            // v0.1.54 — group tags: resolved to active groups inside the
            // mention service; only a bounded, de-duplicated id list travels.
            var mentionedGroupIds = (req.MentionedGroupIds ?? Array.Empty<Guid>())
                .Where(g => g != Guid.Empty).Distinct().Take(20).ToList();
            var metadataJson = mentionedIds.Count > 0 || mentionedMailboxIds.Count > 0 || mentionedGroupIds.Count > 0
                ? JsonSerializer.Serialize(new { mentionedUserIds = mentionedIds, mentionedMailboxIds, mentionedGroupIds })
                : null;

            // Plain-text body derivation (HTML → text when only HTML was
            // sent) happens inside the mutation service so every caller —
            // this endpoint and the bulk action — writes both representations.
            var input = new NewTicketEvent(
                EventType: req.EventType,
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml,
                // A Call defaults to internal like a Note; Comment is always
                // the public side of the Note/Comment pair.
                IsInternal: req.IsInternal ?? (req.EventType is "Note" or "Call"),
                AuthorUserId: userId,
                MetadataJson: metadataJson);
            // v0.1.32 — Insights Workflow: the ticket's place in the tracked
            // views *before* this action changes it (best-effort).
            await workflow.RecordPickupAsync(userId, id, req.EventType, ct);
            var evt = await mutations.AddEventAsync(id, input, ct);
            if (evt is null) return Results.NotFound();
            await workflow.RecordTemplateUseAsync(id, evt.Id, evt.EventType, evt.BodyHtml,
                http.User.FindFirst(ClaimTypes.Email)?.Value, ct);

            // Re-link any user-uploaded attachments to the freshly-created
            // event. Ownership-guarded inside the repo: attempting to attach
            // a file owned by another ticket is a no-op (returned count
            // simply doesn't match), so a hostile payload can't graft
            // someone else's attachment onto this ticket. Failures here are
            // not worth rolling the event back over — we log and continue.
            if (req.AttachmentIds is { Count: > 0 } attIds)
            {
                var moved = await attachmentsRepo.ReassignToEventAsync(attIds, id, evt.Id, ct);
                if (moved != attIds.Count)
                {
                    // Some ids didn't match — almost always a stale draft.
                    // Logged as info because it's not an attack signal.
                }
            }

            // Audit → realtime → SLA → triggers, shared with the bulk path.
            await mutations.PublishEventAsync(actor, id, evt, new
            {
                evt.EventType,
                evt.IsInternal,
                attachmentCount = req.AttachmentIds?.Count ?? 0,
                mentionedUserCount = mentionedIds.Count,
                mentionedMailboxCount = mentionedMailboxIds.Count,
                mentionedGroupCount = mentionedGroupIds.Count,
            }, ct);

            // @@-mention notification raamwerk (v0.0.12 stap 4). Fire-and-forget
            // semantics: the service logs + swallows everything. The request
            // result is already decided, so an errant notifier can't 500 us.
            if (mentionedIds.Count > 0 || mentionedMailboxIds.Count > 0 || mentionedGroupIds.Count > 0)
            {
                var sourceEmail = http.User.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
                await mentionService.PublishAsync(new MentionNotificationSource(
                    TicketId: id,
                    TicketNumber: ticket.Number,
                    TicketSubject: ticket.Subject,
                    QueueId: ticket.QueueId,
                    EventId: evt.Id,
                    EventType: evt.EventType,
                    SourceUserId: userId,
                    SourceUserEmail: sourceEmail,
                    MentionedUserIds: mentionedIds,
                    BodyHtml: evt.BodyHtml ?? string.Empty,
                    BodyText: evt.BodyText ?? string.Empty,
                    MentionedMailboxIds: mentionedMailboxIds,
                    MentionedGroupIds: mentionedGroupIds), ct);
            }

            // v0.0.38 — compose-template-linked surveys. Fires AFTER the
            // event lands so a Graph hiccup never rolls back the reply
            // itself. Idempotency comes from the dispatch service.
            if (req.ComposeTemplateIds is { Count: > 0 } tplIds)
            {
                await surveyTemplateDispatcher.DispatchForTemplatesAsync(id, tplIds, userId, ct);
            }

            // v0.1.17 — logging a phone call clears the Call-back flag
            // (setting Tickets.CallbackClearOnCall). Runs through the normal
            // field-update path so it gets its TicketFlagChange event, audit,
            // realtime push and trigger evaluation like a manual toggle.
            if (req.EventType == "Call" && ticket.IsCallback)
            {
                bool clearOnCall;
                clearOnCall = await settings.GetOrAsync<bool>(SettingKeys.Tickets.CallbackClearOnCall, true, ct);
                if (clearOnCall)
                {
                    var clear = new TicketFieldUpdate(IsCallback: false);
                    if (await mutations.ApplyFieldUpdateAsync(id, clear, userId, ct) is not null)
                    {
                        await mutations.PublishFieldUpdateAsync(
                            actor, id, clear,
                            auditPayload: new { isCallback = false, reason = "call_logged", callEventId = evt.Id },
                            changeSet: null, ct);
                    }
                }
            }

            return Results.Created($"/api/tickets/{id}/events/{evt.Id}", evt);
        }).WithName("AddTicketEvent");

        group.MapPut("/{id:guid}/events/{eventId:long}", async (
            Guid id, long eventId, [FromBody] UpdateEventRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IAttachmentRepository attachmentsRepo,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, IWorkflowCaptureService workflow,
            Servicedesk.Infrastructure.Tickets.ITicketHtmlSanitizer htmlSanitizer,
            CancellationToken ct) =>
        {
            req = req with { BodyHtml = htmlSanitizer.Sanitize(req.BodyHtml) };
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Images pasted while editing arrive as staged uploads. An edit
            // touches one note, so a handful is plenty; anything larger is a
            // malformed or hostile payload.
            if (req.AttachmentIds is { Count: > MaxEditAttachmentIds })
                return Results.BadRequest(new { error = $"At most {MaxEditAttachmentIds} attachments per edit." });

            // Queue-access check via parent ticket
            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (ticket is null) return Results.NotFound();
            var input = new UpdateTicketEvent(
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml,
                IsInternal: req.IsInternal,
                EditorUserId: userId);
            var updated = await tickets.UpdateEventAsync(id, eventId, input, ct);
            if (updated is null) return Results.NotFound();
            // v0.1.32 — an edit can fill a template in (or strip it out).
            await workflow.RecordTemplateUseAsync(id, eventId, updated.EventType, updated.BodyHtml,
                http.User.FindFirst(ClaimTypes.Email)?.Value, ct);

            // Same ownership-guarded re-link as a new note: only uploads still
            // staged on *this* ticket (no event yet) move; foreign ids no-op.
            var linkedAttachments = 0;
            if (req.AttachmentIds is { Count: > 0 } attIds)
                linkedAttachments = await attachmentsRepo.ReassignToEventAsync(attIds, id, eventId, ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.event.edited",
                Actor: actor,
                ActorRole: role,
                Target: $"{id}/events/{eventId}",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { updated.EventType, updated.IsInternal, linkedAttachments }));

            // Notify viewers of this ticket
            await hub.Clients.Group($"ticket:{id}").SendAsync("TicketUpdated", id.ToString(), ct);

            return Results.Ok(updated);
        }).WithName("UpdateTicketEvent");

        group.MapGet("/{id:guid}/events/{eventId:long}/revisions", async (
            Guid id, long eventId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Queue-access check via parent ticket
            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, id, ct);
            if (ticket is null) return Results.NotFound();

            var revisions = await tickets.GetEventRevisionsAsync(id, eventId, ct);
            return Results.Ok(revisions);
        }).WithName("GetEventRevisions");

        // Hover preview of the note a personal or group tag points to. The tag
        // lists are scoped by recipient only, so the full body is read here
        // behind queue access: no access, a deleted ticket, an unknown event
        // or an event type a tag can't come from all answer 404, and the
        // client falls back to the stored plain-text preview.
        group.MapGet("/{id:guid}/events/{eventId:long}/preview", async (
            Guid id, long eventId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, id, ct);
            if (ticket is null) return Results.NotFound();

            var ev = await tickets.GetEventAsync(id, eventId, ct);
            if (ev is null
                || !TagPreviewEventTypes.Contains(ev.EventType)
                || HiddenTimelineEventTypes(role).Contains(ev.EventType))
                return Results.NotFound();

            return Results.Ok(new
            {
                ticketNumber = ticket.Number,
                ticketSubject = ticket.Subject,
                eventId = ev.Id,
                eventType = ev.EventType,
                isInternal = ev.IsInternal,
                authorName = ev.AuthorName,
                createdUtc = ev.CreatedUtc,
                editedUtc = ev.EditedUtc,
                bodyHtml = ev.BodyHtml ?? string.Empty,
            });
        }).WithName("GetTicketEventPreview");

        // ── Pin / Unpin events ──

        group.MapPost("/{id:guid}/events/{eventId:long}/pin", async (
            Guid id, long eventId, [FromBody] PinEventRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (ticket is null) return Results.NotFound();

            var pin = await tickets.PinEventAsync(id, eventId, userId, req.Remark ?? "", ct);
            if (pin is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.event.pinned",
                Actor: actor,
                ActorRole: role,
                Target: $"{id}/events/{eventId}",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { eventId, pin.Remark }));

            await hub.Clients.Group($"ticket:{id}").SendAsync("TicketUpdated", id.ToString(), ct);
            return Results.Ok(pin);
        }).WithName("PinTicketEvent");

        group.MapDelete("/{id:guid}/events/{eventId:long}/pin", async (
            Guid id, long eventId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (ticket is null) return Results.NotFound();

            var deleted = await tickets.UnpinEventAsync(id, eventId, ct);
            if (!deleted) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.event.unpinned",
                Actor: actor,
                ActorRole: role,
                Target: $"{id}/events/{eventId}",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { eventId }));

            await hub.Clients.Group($"ticket:{id}").SendAsync("TicketUpdated", id.ToString(), ct);
            return Results.NoContent();
        }).WithName("UnpinTicketEvent");

        group.MapPatch("/{id:guid}/events/{eventId:long}/pin", async (
            Guid id, long eventId, [FromBody] UpdatePinRemarkRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (ticket is null) return Results.NotFound();

            var pin = await tickets.UpdatePinRemarkAsync(id, eventId, req.Remark, ct);
            if (pin is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.event.pin_updated",
                Actor: actor,
                ActorRole: role,
                Target: $"{id}/events/{eventId}",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { eventId, pin.Remark }));

            await hub.Clients.Group($"ticket:{id}").SendAsync("TicketUpdated", id.ToString(), ct);
            return Results.Ok(pin);
        }).WithName("UpdatePinRemark");

        return app;
    }
}
