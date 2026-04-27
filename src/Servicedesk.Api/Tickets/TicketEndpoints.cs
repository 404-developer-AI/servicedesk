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
using Servicedesk.Infrastructure.Triggers;

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
            ICompanyRepository companies, IMailTimelineEnricher mailEnricher,
            [FromServices] Npgsql.NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var detail = await repo.GetByIdAsync(id, ct);
            if (detail is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, detail.Ticket.QueueId, ct))
                return Results.NotFound(); // 404 to prevent existence leaking

            detail = await mailEnricher.EnrichAsync(detail, ct);
            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.RequesterContactId, ct);

            // v0.0.23: surface merge metadata so the UI can render the banners
            // ("Merged into #X" on the source, "Merged from #A, #B" on the
            // target). One round-trip each — both index-only at scale.
            var mergedSourceNumbers = await repo.GetMergedSourceTicketNumbersAsync(id, ct);
            var splitChildren = await repo.GetSplitChildrenAsync(id, ct);
            string? mergedByUserName = null;
            string? mergedIntoTicketNumber = null;
            string? splitFromTicketNumber = null;
            string? splitFromUserName = null;
            if (detail.Ticket.MergedByUserId is not null
                || detail.Ticket.MergedIntoTicketId is not null
                || detail.Ticket.SplitFromTicketId is not null
                || detail.Ticket.SplitFromUserId is not null)
            {
                await using var conn = await dataSource.OpenConnectionAsync(ct);
                if (detail.Ticket.MergedByUserId is { } actorId)
                {
                    mergedByUserName = await Dapper.SqlMapper.ExecuteScalarAsync<string?>(conn,
                        new Dapper.CommandDefinition(
                            "SELECT email FROM users WHERE id = @id",
                            new { id = actorId }, cancellationToken: ct));
                }
                if (detail.Ticket.MergedIntoTicketId is { } targetId)
                {
                    var targetNumber = await Dapper.SqlMapper.ExecuteScalarAsync<long?>(conn,
                        new Dapper.CommandDefinition(
                            "SELECT number FROM tickets WHERE id = @id",
                            new { id = targetId }, cancellationToken: ct));
                    if (targetNumber is { } n) mergedIntoTicketNumber = n.ToString();
                }
                if (detail.Ticket.SplitFromTicketId is { } parentId)
                {
                    var parentNumber = await Dapper.SqlMapper.ExecuteScalarAsync<long?>(conn,
                        new Dapper.CommandDefinition(
                            "SELECT number FROM tickets WHERE id = @id",
                            new { id = parentId }, cancellationToken: ct));
                    if (parentNumber is { } n) splitFromTicketNumber = n.ToString();
                }
                if (detail.Ticket.SplitFromUserId is { } splitActorId)
                {
                    splitFromUserName = await Dapper.SqlMapper.ExecuteScalarAsync<string?>(conn,
                        new Dapper.CommandDefinition(
                            "SELECT email FROM users WHERE id = @id",
                            new { id = splitActorId }, cancellationToken: ct));
                }
            }

            // For tickets created via split, surface the source-mail's non-inline
            // attachments so the description block can render download chips.
            // The bytes stay on the source mail; the URLs route through the
            // source ticket's mail-attachment endpoint, which the agent can
            // already access (split requires queue access on the source).
            var descriptionAttachments = await BuildSplitDescriptionAttachmentsAsync(
                detail, dataSource, ct);

            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                events = detail.Events,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
                mergedSourceTicketNumbers = mergedSourceNumbers,
                mergedByUserName,
                mergedIntoTicketNumber,
                splitFromTicketNumber,
                splitFromUserName,
                splitChildren = splitChildren.Select(c => new { id = c.Id, number = c.Number }),
                descriptionAttachments,
            });
        }).WithName("GetTicket").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] CreateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IContactLookupService contactLookup,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla,
            ITriggerService triggers, CancellationToken ct) =>
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

            // Trigger evaluator (v0.0.24 Blok 2). Runs after SLA so an
            // action handler that consults SLA-derived fields sees the
            // current deadlines. AllFieldsNew + ArticleAdded=true is the
            // ChangeSet for a fresh ticket — every field is "changed" and
            // a description-event was just written.
            await triggers.EvaluateAsync(
                ticketId: created.Id,
                ticketEventId: null,
                activatorKind: TriggerActivatorKind.Action,
                changeSet: TriggerChangeSet.AllFieldsNew(),
                ct: ct);

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
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla,
            ITriggerService triggerService, CancellationToken ct) =>
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

            // Trigger evaluator (v0.0.24 Blok 2). Build a ChangeSet from
            // the PATCH request: any non-null field counts as "changed"
            // (a no-op same-value PATCH still trips the Selective check,
            // refining that costs an extra pre-update fetch we don't pay
            // for elsewhere).
            var changedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (req.QueueId.HasValue) changedFields.Add(TriggerFieldKeys.TicketQueueId);
            if (req.StatusId.HasValue) changedFields.Add(TriggerFieldKeys.TicketStatusId);
            if (req.PriorityId.HasValue) changedFields.Add(TriggerFieldKeys.TicketPriorityId);
            if (req.CategoryId.HasValue) changedFields.Add(TriggerFieldKeys.TicketCategoryId);
            if (req.AssigneeUserId.HasValue) changedFields.Add(TriggerFieldKeys.TicketOwnerId);
            if (req.Subject is not null) changedFields.Add(TriggerFieldKeys.TicketSubject);
            await triggerService.EvaluateAsync(
                ticketId: id,
                ticketEventId: null,
                activatorKind: TriggerActivatorKind.Action,
                changeSet: new TriggerChangeSet(changedFields, ArticleAdded: false),
                ct: ct);

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
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla,
            ITriggerService triggerService, CancellationToken ct) =>
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

            // Trigger evaluator (v0.0.24 Blok 2). A new article was just
            // added — that satisfies the Selective short-circuit by
            // itself, so no per-field changedFields tracking needed here.
            await triggerService.EvaluateAsync(
                ticketId: id,
                ticketEventId: evt.Id,
                activatorKind: TriggerActivatorKind.Action,
                changeSet: TriggerChangeSet.ArticleOnly(),
                ct: ct);

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

        // v0.0.23 ticket merge: lightweight typeahead used by the merge dialog
        // to pick a target ticket. Filters out the source ticket, soft-deleted
        // rows, and any ticket that is itself already merged. Queue-access is
        // enforced server-side: an agent cannot merge into a queue they can't
        // see, regardless of what the client sends.
        group.MapGet("/picker", async (
            string? q, Guid? excludeTicketId, int? limit,
            HttpContext http, ITicketRepository repo, IQueueAccessService queueAccess,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

            IReadOnlyList<Guid>? accessibleQueueIds = null;
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                accessibleQueueIds = await queueAccess.GetAccessibleQueueIdsAsync(userId, role, ct);
                if (accessibleQueueIds.Count == 0)
                    return Results.Ok(new { items = Array.Empty<object>() });
            }

            var hits = await repo.SearchPickerAsync(
                search: q,
                excludeTicketId: excludeTicketId ?? Guid.Empty,
                accessibleQueueIds: accessibleQueueIds,
                limit: limit ?? 20,
                ct: ct);
            return Results.Ok(new { items = hits });
        }).WithName("PickTicket").WithOpenApi();

        // v0.0.23 ticket merge endpoint. Idempotency-by-construction: if the
        // source is already merged the repository returns AlreadyMerged and we
        // 409 instead of silently re-running. Cross-customer merges require
        // explicit acknowledgement from the caller — the dialog flips the flag
        // when the requester or company differs and the agent confirms.
        group.MapPost("/{id:guid}/merge", async (
            Guid id, [FromBody] MergeTicketRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.TargetTicketId == Guid.Empty)
                return Results.BadRequest(new { error = "targetTicketId is required." });
            if (req.TargetTicketId == id)
                return Results.BadRequest(new { error = "A ticket cannot be merged into itself." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // Both tickets must be visible to the actor through queue-access.
            // Admins bypass; agents who can't see one side get a 404 so we
            // don't leak which ticket exists in a forbidden queue.
            var source = await tickets.GetByIdAsync(id, ct);
            if (source is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, source.Ticket.QueueId, ct))
                return Results.NotFound();

            var target = await tickets.GetByIdAsync(req.TargetTicketId, ct);
            if (target is null)
                return Results.BadRequest(new { error = "Target ticket not found." });
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, target.Ticket.QueueId, ct))
                return Results.Json(
                    new { error = "You do not have access to the target ticket's queue.", code = "queue_forbidden" },
                    statusCode: 403);

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

            return Results.Ok(new
            {
                targetTicketId = req.TargetTicketId,
                sourceNumber = result.SourceNumber,
                targetNumber = result.TargetNumber,
                movedEventCount = result.MovedEventCount,
                crossCustomer = result.CrossCustomer,
            });
        }).WithName("MergeTicket").WithOpenApi();

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

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var source = await tickets.GetByIdAsync(id, ct);
            if (source is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, source.Ticket.QueueId, ct))
                return Results.NotFound();

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
        }).WithName("SplitTicket").WithOpenApi();

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

    /// v0.0.23: payload for merging this ticket into another. The acknowledge
    /// flag is required only when the dialog detected a cross-customer or
    /// cross-company merge — the server still re-validates from its own data.
    public sealed record MergeTicketRequest(
        Guid TargetTicketId,
        bool AcknowledgedCrossCustomer);

    /// v0.0.23: payload for splitting a single received-mail off this ticket
    /// into a brand-new ticket with the same requester and system defaults
    /// for queue/priority/status. The agent fills the title manually.
    public sealed record SplitTicketRequest(
        long SourceMailEventId,
        string NewSubject);

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

    /// v0.0.23 split: when this ticket was created by splitting a mail off
    /// another ticket, surface the source-mail's non-inline attachments so the
    /// description block can render download chips. URLs route through the
    /// source ticket's per-mail attachment endpoint — the bytes never move.
    /// Returns an empty list for tickets that aren't splits.
    private static async Task<IReadOnlyList<object>> BuildSplitDescriptionAttachmentsAsync(
        TicketDetail detail, Npgsql.NpgsqlDataSource dataSource, CancellationToken ct)
    {
        if (detail.Ticket.SplitFromTicketId is null) return Array.Empty<object>();

        // Created event carries splitFromMailMessageId in its metadata (set by
        // SplitAsync). Locate it and parse out the source mail id.
        var createdEvent = detail.Events.FirstOrDefault(e => e.EventType == "Created");
        if (createdEvent is null || string.IsNullOrWhiteSpace(createdEvent.MetadataJson))
            return Array.Empty<object>();

        Guid? sourceMailMessageId = null;
        try
        {
            using var doc = JsonDocument.Parse(createdEvent.MetadataJson);
            if (doc.RootElement.TryGetProperty("splitFromMailMessageId", out var prop)
                && prop.ValueKind == JsonValueKind.String
                && Guid.TryParse(prop.GetString(), out var g))
            {
                sourceMailMessageId = g;
            }
        }
        catch { /* malformed metadata — treat as no attachments */ }

        if (sourceMailMessageId is null) return Array.Empty<object>();

        const string sql = """
            SELECT a.id, a.original_filename, a.mime_type, a.size_bytes
            FROM attachments a
            WHERE a.owner_kind = 'Mail'
              AND a.owner_id = @mailId
              AND a.processing_state = 'Ready'
              AND a.is_inline = FALSE
            ORDER BY a.created_utc, a.id
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await Dapper.SqlMapper.QueryAsync<(Guid Id, string OriginalFilename, string MimeType, long SizeBytes)>(
            conn, new Dapper.CommandDefinition(sql, new { mailId = sourceMailMessageId.Value }, cancellationToken: ct));

        var sourceTicketId = detail.Ticket.SplitFromTicketId.Value;
        var mailId = sourceMailMessageId.Value;
        return rows.Select(r => (object)new
        {
            id = r.Id,
            name = r.OriginalFilename,
            mimeType = r.MimeType,
            size = r.SizeBytes,
            url = $"/api/tickets/{sourceTicketId}/mail/{mailId}/attachments/{r.Id}",
        }).ToList();
    }
}
