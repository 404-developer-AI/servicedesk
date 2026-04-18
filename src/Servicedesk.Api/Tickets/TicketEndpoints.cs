using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Ingest;
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

        group.MapPost("/{id:guid}/events", async (
            Guid id, [FromBody] AddEventRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
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
            var input = new NewTicketEvent(
                EventType: req.EventType,
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml,
                IsInternal: req.IsInternal ?? (req.EventType == "Note"),
                AuthorUserId: userId);
            var evt = await tickets.AddEventAsync(id, input, ct);
            if (evt is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.event.added",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { evt.EventType, evt.IsInternal }));

            // Notify viewers of this ticket + the ticket list
            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            await sla.OnTicketEventAsync(id, evt.EventType, ct);

            return Results.Created($"/api/tickets/{id}/events/{evt.Id}", evt);
        }).WithName("AddTicketEvent").WithOpenApi();

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
        bool? IsInternal);

    public sealed record UpdateEventRequest(
        string? BodyText,
        string? BodyHtml,
        bool? IsInternal);

    public sealed record PinEventRequest(string? Remark);

    public sealed record UpdatePinRemarkRequest(string Remark);

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
