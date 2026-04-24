using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;

namespace Servicedesk.Api.Tickets;

/// v0.0.5 ticket API surface: list/get/create backed by the Dapper
/// repository. The list page uses keyset pagination — no offset, so it
/// stays fast as the dataset grows. Tickets are Agent+Admin only until the
/// customer portal lands. The dev-only benchmark + seed endpoints sit
/// behind an <see cref="IWebHostEnvironment.IsDevelopment"/> gate so they
/// cannot ship to production.
public static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", async (
            Guid? queueId, Guid? statusId, Guid? priorityId, Guid? assigneeUserId,
            Guid? requesterContactId, string? search, bool? openOnly,
            string? sortField, string? sortDirection, bool? priorityFloat, int? offset,
            DateTime? cursorUpdatedUtc, Guid? cursorId, int? limit,
            HttpContext http, ITicketRepository repo, IQueueAccessService queueAccess, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Queue-access enforcement: admins get null (no filter), agents
            // get their assigned queue list. An agent with zero queues gets
            // an empty page immediately.
            IReadOnlyList<Guid>? accessibleQueueIds = null;
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                accessibleQueueIds = await queueAccess.GetAccessibleQueueIdsAsync(userId, role, ct);
                if (accessibleQueueIds.Count == 0)
                    return Results.Ok(new { items = Array.Empty<object>(), nextCursor = (object?)null, nextOffset = (int?)null });
            }

            var q = new TicketQuery(
                QueueId: queueId, StatusId: statusId, PriorityId: priorityId,
                AssigneeUserId: assigneeUserId, RequesterContactId: requesterContactId,
                Search: search, OpenOnly: openOnly ?? false,
                SortField: sortField, SortDirection: sortDirection,
                PriorityFloat: priorityFloat ?? false, Offset: offset,
                CursorUpdatedUtc: cursorUpdatedUtc, CursorId: cursorId,
                Limit: limit ?? 50,
                AccessibleQueueIds: accessibleQueueIds);
            var page = await repo.SearchAsync(q, VisibilityScope.All, null, null, ct);
            return Results.Ok(new
            {
                items = page.Items,
                nextCursor = page.NextCursorUpdatedUtc.HasValue && page.NextCursorId.HasValue
                    ? new { updatedUtc = page.NextCursorUpdatedUtc, id = page.NextCursorId }
                    : null,
                nextOffset = page.NextOffset,
            });
        }).WithName("ListTickets").WithOpenApi();

        group.MapGet("/{id:guid}", async (
            Guid id, HttpContext http, ITicketRepository repo, IQueueAccessService queueAccess,
            ICompanyRepository companies, IMailTimelineEnricher mailEnricher, CancellationToken ct) =>
        {
            var detail = await repo.GetByIdAsync(id, ct);
            if (detail is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, detail.Ticket.QueueId, ct))
                return Results.NotFound(); // 404 to prevent existence leaking

            detail = await mailEnricher.EnrichAsync(detail, ct);
            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                events = detail.Events,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("GetTicket").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] CreateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IContactLookupService contactLookup,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Subject))
                return Results.BadRequest(new { error = "Subject is required." });
            if (req.RequesterContactId == Guid.Empty)
                return Results.BadRequest(new { error = "requesterContactId is required." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, req.QueueId, ct))
                return Results.Json(new { error = "You do not have access to this queue." }, statusCode: 403);

            var requester = await companies.GetContactAsync(req.RequesterContactId, ct);
            if (requester is null) return Results.BadRequest(new { error = "Unknown requester contact." });

            // Run the same resolution tree the mail-intake uses so an
            // agent-created ticket freezes its company_id identically. An
            // ambiguous contact → awaiting_company_assignment=true and the
            // Ticket dialog (ToDo #4) prompts the agent to pick explicitly.
            var resolution = await contactLookup.ResolveCompanyForNewTicketAsync(req.RequesterContactId, ct);

            var created = await tickets.CreateAsync(new NewTicket(
                Subject: req.Subject.Trim(),
                BodyText: req.BodyText ?? "",
                BodyHtml: req.BodyHtml,
                RequesterContactId: req.RequesterContactId,
                QueueId: req.QueueId,
                StatusId: req.StatusId,
                PriorityId: req.PriorityId,
                CategoryId: req.CategoryId,
                AssigneeUserId: req.AssigneeUserId,
                Source: req.Source ?? "Api",
                CompanyId: resolution.CompanyId,
                AwaitingCompanyAssignment: resolution.Awaiting,
                CompanyResolvedVia: resolution.ResolvedVia), ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.created",
                Actor: actor,
                ActorRole: role,
                Target: created.Id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { created.Number, created.Subject }));

            // Notify ticket list viewers that a new ticket was created
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", created.Id.ToString(), ct);

            await sla.OnTicketCreatedAsync(created.Id, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, created.RequesterContactId, ct);
            var showAlertOnCreate = companyAlert is not null && companyAlert.AlertOnCreate
                && !string.IsNullOrWhiteSpace(companyAlert.AlertText);
            return Results.Created($"/api/tickets/{created.Id}", new
            {
                ticket = created,
                companyAlert,
                showAlertOnCreate,
            });
        }).WithName("CreateTicket").WithOpenApi();

        group.MapPatch("/{id:guid}", async (
            Guid id, [FromBody] UpdateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Verify the agent can access the ticket's current queue
            var current = await tickets.GetByIdAsync(id, ct);
            if (current is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, current.Ticket.QueueId, ct))
                return Results.NotFound();

            // If moving to a different queue, verify access to the target queue too
            if (req.QueueId.HasValue && req.QueueId != current.Ticket.QueueId)
            {
                if (!await queueAccess.HasQueueAccessAsync(userId, userRole, req.QueueId.Value, ct))
                    return Results.Json(new { error = "You do not have access to the target queue." }, statusCode: 403);
            }

            var update = new TicketFieldUpdate(
                QueueId: req.QueueId,
                StatusId: req.StatusId,
                PriorityId: req.PriorityId,
                CategoryId: req.CategoryId,
                AssigneeUserId: req.AssigneeUserId,
                Subject: req.Subject?.Trim(),
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml);
            var detail = await tickets.UpdateFieldsAsync(id, update, userId, ct);
            if (detail is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.updated",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: req));

            // Notify viewers of this ticket + the ticket list
            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            await sla.OnTicketFieldsChangedAsync(id, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                events = detail.Events,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("UpdateTicket").WithOpenApi();

        // Manual company assignment (v0.0.9 ToDo #4). Triggered when a ticket
        // was created with awaiting_company_assignment=true because the contact
        // had no primary link, multiple secondaries, or supplier-only links.
        // The reason is computed server-side from the contact's current links
        // so the audit payload can't be spoofed from the client.
        group.MapPatch("/{id:guid}/company", async (
            Guid id, [FromBody] AssignTicketCompanyRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.CompanyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId is required." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetByIdAsync(id, ct);
            if (current is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, current.Ticket.QueueId, ct))
                return Results.NotFound();

            var targetCompany = await companies.GetCompanyAsync(req.CompanyId, ct);
            if (targetCompany is null)
                return Results.BadRequest(new { error = "Unknown company." });
            if (!targetCompany.IsActive)
                return Results.BadRequest(new { error = "Cannot assign an inactive company." });

            // Infer the resolution reason from the contact's current link set.
            // supplier_only → contact has links but none are primary/secondary.
            // ambiguous_secondary → everything else that landed in awaiting
            // (multiple secondaries, mixed, etc.). Agents overriding an already
            // resolved ticket get reason='override'.
            string reason;
            if (current.Ticket.AwaitingCompanyAssignment)
            {
                var links = await companies.ListContactLinksAsync(current.Ticket.RequesterContactId, ct);
                reason = links.All(l => l.Role == "supplier") && links.Count > 0
                    ? "supplier_only"
                    : "ambiguous_secondary";
            }
            else
            {
                reason = "override";
            }

            var detail = await tickets.AssignCompanyAsync(id, req.CompanyId, userId, ct);
            if (detail is null) return Results.NotFound();

            // Optional side-effect: link the requester contact to this company
            // as 'supplier' so the learn-flow grows the contact's link set
            // without stepping on an existing primary/secondary.
            if (req.LinkAsSupplier)
            {
                var existingLinks = await companies.ListContactLinksAsync(current.Ticket.RequesterContactId, ct);
                var alreadyLinked = existingLinks.Any(l => l.CompanyId == req.CompanyId);
                if (!alreadyLinked)
                {
                    await companies.UpsertContactLinkAsync(
                        current.Ticket.RequesterContactId, req.CompanyId, "supplier", ct);
                }
            }

            var (actor, actorRole) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.company.manually_assigned",
                Actor: actor,
                ActorRole: actorRole,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    companyId = req.CompanyId,
                    companyName = targetCompany.Name,
                    previousCompanyId = current.Ticket.CompanyId,
                    reason,
                    linkAsSupplier = req.LinkAsSupplier,
                    contactId = current.Ticket.RequesterContactId,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                events = detail.Events,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("AssignTicketCompany").WithOpenApi();

        // v0.0.12: switch the requester on an ongoing ticket. The new contact's
        // company is re-resolved with the same decision tree used at ticket
        // creation (primary → single secondary → supplier_only → none), so the
        // ticket's frozen company_id follows the new requester. Writes a
        // RequesterChange timeline event with from/to contact + company.
        group.MapPatch("/{id:guid}/requester", async (
            Guid id, [FromBody] ChangeTicketRequesterRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IContactLookupService contactLookup,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla, CancellationToken ct) =>
        {
            if (req.ContactId == Guid.Empty)
                return Results.BadRequest(new { error = "contactId is required." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetByIdAsync(id, ct);
            if (current is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, current.Ticket.QueueId, ct))
                return Results.NotFound();

            var contact = await companies.GetContactAsync(req.ContactId, ct);
            if (contact is null)
                return Results.BadRequest(new { error = "Unknown contact." });
            if (!contact.IsActive)
                return Results.BadRequest(new { error = "Cannot assign an inactive contact." });

            var resolution = await contactLookup.ResolveCompanyForNewTicketAsync(req.ContactId, ct);

            var detail = await tickets.ChangeRequesterAsync(
                ticketId: id,
                newContactId: req.ContactId,
                newCompanyId: resolution.CompanyId,
                awaitingCompanyAssignment: resolution.Awaiting,
                companyResolvedVia: resolution.ResolvedVia,
                actorUserId: userId,
                ct: ct);
            if (detail is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.requester.changed",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    fromContactId = current.Ticket.RequesterContactId,
                    toContactId = req.ContactId,
                    fromCompanyId = current.Ticket.CompanyId,
                    toCompanyId = resolution.CompanyId,
                    resolvedVia = resolution.ResolvedVia,
                    awaiting = resolution.Awaiting,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            await sla.OnTicketFieldsChangedAsync(id, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                events = detail.Events,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("ChangeTicketRequester").WithOpenApi();

        group.MapPost("/{id:guid}/events", async (
            Guid id, [FromBody] AddEventRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IAttachmentRepository attachmentsRepo, IUserService users,
            IMentionNotificationService mentionService,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.EventType))
                return Results.BadRequest(new { error = "eventType is required." });
            if (req.EventType != "Comment" && req.EventType != "Note")
                return Results.BadRequest(new { error = "eventType must be 'Comment' or 'Note'." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Queue-access check via parent ticket
            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

            // @@-mention filtering (v0.0.12 stap 3): unknown ids, customer ids,
            // or deleted-user ids are silently dropped. Same soft-drop semantics
            // as the attachment-ownership guard — a stale draft mentioning an
            // ex-agent shouldn't fail the submit.
            IReadOnlyList<Guid> mentionedIds = Array.Empty<Guid>();
            if (req.MentionedUserIds is { Count: > 0 } incomingMentions)
            {
                mentionedIds = await users.FilterAgentIdsAsync(incomingMentions, ct);
            }
            var metadataJson = mentionedIds.Count > 0
                ? JsonSerializer.Serialize(new { mentionedUserIds = mentionedIds })
                : null;

            var input = new NewTicketEvent(
                EventType: req.EventType,
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml,
                IsInternal: req.IsInternal ?? (req.EventType == "Note"),
                AuthorUserId: userId,
                MetadataJson: metadataJson);
            var evt = await tickets.AddEventAsync(id, input, ct);
            if (evt is null) return Results.NotFound();

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

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.event.added",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    evt.EventType,
                    evt.IsInternal,
                    attachmentCount = req.AttachmentIds?.Count ?? 0,
                    mentionedUserCount = mentionedIds.Count,
                }));

            // Notify viewers of this ticket + the ticket list
            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            await sla.OnTicketEventAsync(id, evt.EventType, ct);

            // @@-mention notification raamwerk (v0.0.12 stap 4). Fire-and-forget
            // semantics: the service logs + swallows everything. The request
            // result is already decided, so an errant notifier can't 500 us.
            if (mentionedIds.Count > 0)
            {
                var sourceEmail = http.User.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
                await mentionService.PublishAsync(new MentionNotificationSource(
                    TicketId: id,
                    TicketNumber: ticket.Ticket.Number,
                    TicketSubject: ticket.Ticket.Subject,
                    QueueId: ticket.Ticket.QueueId,
                    EventId: evt.Id,
                    EventType: evt.EventType,
                    SourceUserId: userId,
                    SourceUserEmail: sourceEmail,
                    MentionedUserIds: mentionedIds,
                    BodyHtml: evt.BodyHtml ?? string.Empty,
                    BodyText: evt.BodyText ?? string.Empty), ct);
            }

            return Results.Created($"/api/tickets/{id}/events/{evt.Id}", evt);
        }).WithName("AddTicketEvent").WithOpenApi();

        group.MapPost("/{id:guid}/mail", async (
            Guid id, [FromBody] SendOutboundMailRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IOutboundMailService outbound, IHubContext<TicketPresenceHub> hub,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

            if (!Enum.TryParse<OutboundMailKind>(req.Kind, ignoreCase: true, out var kind))
                return Results.BadRequest(new { error = "kind must be Reply, ReplyAll, or New." });

            var request = new OutboundMailRequest(
                TicketId: id,
                AuthorUserId: userId,
                Kind: kind,
                To: MapRecipients(req.To),
                Cc: MapRecipients(req.Cc),
                Bcc: MapRecipients(req.Bcc),
                Subject: req.Subject ?? string.Empty,
                BodyHtml: req.BodyHtml ?? string.Empty,
                AttachmentIds: req.AttachmentIds,
                MentionedUserIds: req.MentionedUserIds,
                LinkedFormIds: req.LinkedFormIds);

            var result = await outbound.SendAsync(request, ct);
            switch (result.Status)
            {
                case OutboundMailStatus.TicketNotFound:
                    return Results.NotFound();
                case OutboundMailStatus.NoMailboxConfigured:
                    return Results.BadRequest(new { error = result.ErrorMessage });
                case OutboundMailStatus.InvalidRequest:
                    return Results.BadRequest(new { error = result.ErrorMessage });
                case OutboundMailStatus.AttachmentTooLarge:
                    return Results.Json(new { error = result.ErrorMessage }, statusCode: 413);
            }

            var evt = result.Event!;
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.mail.sent",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    kind = kind.ToString(),
                    toCount = request.To.Count,
                    ccCount = request.Cc.Count,
                    bccCount = request.Bcc.Count,
                    subject = request.Subject,
                    attachmentCount = req.AttachmentIds?.Count ?? 0,
                    mentionedUserCount = result.MentionedUserCount,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            return Results.Created($"/api/tickets/{id}/events/{evt.Id}", evt);
        }).WithName("SendTicketMail").WithOpenApi();

        group.MapPut("/{id:guid}/events/{eventId:long}", async (
            Guid id, long eventId, [FromBody] UpdateEventRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Queue-access check via parent ticket
            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, ticket.Ticket.QueueId, ct))
                return Results.NotFound();
            var input = new UpdateTicketEvent(
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml,
                IsInternal: req.IsInternal,
                EditorUserId: userId);
            var updated = await tickets.UpdateEventAsync(id, eventId, input, ct);
            if (updated is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.event.edited",
                Actor: actor,
                ActorRole: role,
                Target: $"{id}/events/{eventId}",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { updated.EventType, updated.IsInternal }));

            // Notify viewers of this ticket
            await hub.Clients.Group($"ticket:{id}").SendAsync("TicketUpdated", id.ToString(), ct);

            return Results.Ok(updated);
        }).WithName("UpdateTicketEvent").WithOpenApi();

        group.MapGet("/{id:guid}/events/{eventId:long}/revisions", async (
            Guid id, long eventId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Queue-access check via parent ticket
            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

            var revisions = await tickets.GetEventRevisionsAsync(id, eventId, ct);
            return Results.Ok(revisions);
        }).WithName("GetEventRevisions").WithOpenApi();

        // ── Pin / Unpin events ──

        group.MapPost("/{id:guid}/events/{eventId:long}/pin", async (
            Guid id, long eventId, [FromBody] PinEventRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

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
        }).WithName("PinTicketEvent").WithOpenApi();

        group.MapDelete("/{id:guid}/events/{eventId:long}/pin", async (
            Guid id, long eventId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

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
        }).WithName("UnpinTicketEvent").WithOpenApi();

        group.MapPatch("/{id:guid}/events/{eventId:long}/pin", async (
            Guid id, long eventId, [FromBody] UpdatePinRemarkRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

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
        }).WithName("UpdatePinRemark").WithOpenApi();

        return app;
    }

    /// Development-only seed + benchmark harness. Gated by the hosting
    /// environment so the routes literally don't exist in production
    /// builds. Used to prove list/search/detail stay fast at 1M rows.
    public static IEndpointRouteBuilder MapDevBenchmarkEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment env)
    {
        if (!env.IsDevelopment()) return app;

        var group = app.MapGroup("/api/dev")
            .WithTags("Dev")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPost("/seed-tickets", async (int? count, ITicketRepository repo, CancellationToken ct) =>
        {
            var n = Math.Clamp(count ?? 1000, 1, 1_000_000);
            var sw = Stopwatch.StartNew();
            var inserted = await repo.InsertFakeBatchAsync(n, ct);
            sw.Stop();
            return Results.Ok(new
            {
                requested = n,
                inserted,
                elapsedMs = sw.ElapsedMilliseconds,
                rowsPerSecond = inserted / Math.Max(1, sw.Elapsed.TotalSeconds),
            });
        }).WithName("DevSeedTickets").WithOpenApi();

        group.MapGet("/benchmarks", async (ITicketRepository repo, CancellationToken ct) =>
        {
            var results = new List<object>();

            async Task Measure(string label, Func<Task> action)
            {
                var sw = Stopwatch.StartNew();
                await action();
                sw.Stop();
                results.Add(new { label, elapsedMs = sw.Elapsed.TotalMilliseconds });
            }

            await Measure("list first 50 (all queues)", async () =>
            {
                await repo.SearchAsync(new TicketQuery(Limit: 50), VisibilityScope.All, null, null, ct);
            });

            await Measure("list open only 50", async () =>
            {
                await repo.SearchAsync(new TicketQuery(Limit: 50, OpenOnly: true), VisibilityScope.All, null, null, ct);
            });

            await Measure("search subject contains 'ticket'", async () =>
            {
                await repo.SearchAsync(new TicketQuery(Search: "ticket", Limit: 50), VisibilityScope.All, null, null, ct);
            });

            await Measure("open counts by queue", async () =>
            {
                await repo.GetOpenCountsByQueueAsync(ct);
            });

            return Results.Ok(new { benchmarks = results });
        }).WithName("DevBenchmarks").WithOpenApi();

        group.MapPost("/seed-agent", async (
            [FromQuery] string email,
            [FromQuery] string password,
            [FromServices] IPasswordHasher hasher,
            [FromServices] Npgsql.NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var hash = hasher.Hash(password);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var id = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(conn, new Dapper.CommandDefinition(
                """
                INSERT INTO users (email, password_hash, role_name)
                VALUES (@email, @hash, 'Agent')
                ON CONFLICT (email) DO NOTHING
                RETURNING id
                """,
                new { email, hash }, cancellationToken: ct));
            if (id == Guid.Empty)
                return Results.Conflict(new { error = "User already exists" });

            return Results.Ok(new { id, email, role = "Agent" });
        }).WithName("DevSeedAgent").WithOpenApi();

        return app;
    }

    public sealed record CreateTicketRequest(
        [property: Required] string Subject,
        string? BodyText,
        string? BodyHtml,
        Guid RequesterContactId,
        Guid QueueId,
        Guid StatusId,
        Guid PriorityId,
        Guid? CategoryId,
        Guid? AssigneeUserId,
        string? Source);

    public sealed record UpdateTicketRequest(
        Guid? QueueId,
        Guid? StatusId,
        Guid? PriorityId,
        Guid? CategoryId,
        Guid? AssigneeUserId,
        string? Subject,
        string? BodyText,
        string? BodyHtml);

    public sealed record AddEventRequest(
        string? EventType,
        string? BodyText,
        string? BodyHtml,
        bool? IsInternal,
        IReadOnlyList<Guid>? AttachmentIds = null,
        IReadOnlyList<Guid>? MentionedUserIds = null);

    public sealed record UpdateEventRequest(
        string? BodyText,
        string? BodyHtml,
        bool? IsInternal);

    public sealed record SendOutboundMailRequest(
        string Kind,
        IReadOnlyList<MailRecipientInput>? To,
        IReadOnlyList<MailRecipientInput>? Cc,
        IReadOnlyList<MailRecipientInput>? Bcc,
        string? Subject,
        string? BodyHtml,
        IReadOnlyList<Guid>? AttachmentIds = null,
        IReadOnlyList<Guid>? MentionedUserIds = null,
        IReadOnlyList<Guid>? LinkedFormIds = null);

    public sealed record MailRecipientInput(string Address, string? Name);

    private static IReadOnlyList<GraphRecipient> MapRecipients(IReadOnlyList<MailRecipientInput>? input)
    {
        if (input is null || input.Count == 0) return Array.Empty<GraphRecipient>();
        var list = new List<GraphRecipient>(input.Count);
        foreach (var r in input)
        {
            if (string.IsNullOrWhiteSpace(r.Address)) continue;
            list.Add(new GraphRecipient(r.Address.Trim(), (r.Name ?? r.Address).Trim()));
        }
        return list;
    }

    public sealed record PinEventRequest(string? Remark);

    /// v0.0.9 ToDo #4: manual company-assignment payload. <c>LinkAsSupplier</c>
    /// is the opt-in learn-flow that also adds the requester contact to the
    /// target company as a supplier link in the same call.
    public sealed record AssignTicketCompanyRequest(
        Guid CompanyId,
        bool LinkAsSupplier);

    public sealed record UpdatePinRemarkRequest(string Remark);

    /// v0.0.12: payload for switching a ticket's requester to another contact.
    /// The company re-resolves server-side via <see cref="IContactLookupService"/>,
    /// so the client only needs to pass the target contact id.
    public sealed record ChangeTicketRequesterRequest(Guid ContactId);

    /// v0.0.9: per-company alert surfaced next to a ticket. Non-null only
    /// when the requester's contact is linked to an active company. The
    /// frontend decides whether to actually show a popup based on the
    /// alert_on_create / alert_on_open flags.
    public sealed record TicketCompanyAlert(
        Guid CompanyId,
        string CompanyName,
        string Code,
        string AlertText,
        bool AlertOnCreate,
        bool AlertOnOpen,
        string AlertOnOpenMode);

    private static async Task<TicketCompanyAlert?> BuildCompanyAlertAsync(
        ICompanyRepository companies, Guid requesterContactId, CancellationToken ct)
    {
        var company = await companies.GetPrimaryCompanyForContactAsync(requesterContactId, ct);
        if (company is null) return null;
        return new TicketCompanyAlert(
            CompanyId: company.Id,
            CompanyName: company.Name,
            Code: company.Code,
            AlertText: company.AlertText,
            AlertOnCreate: company.AlertOnCreate,
            AlertOnOpen: company.AlertOnOpen,
            AlertOnOpenMode: company.AlertOnOpenMode);
    }
}
