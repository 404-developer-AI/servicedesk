using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Tickets;

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
            DateTime? cursorUpdatedUtc, Guid? cursorId, int? limit,
            ITicketRepository repo, CancellationToken ct) =>
        {
            var q = new TicketQuery(
                QueueId: queueId, StatusId: statusId, PriorityId: priorityId,
                AssigneeUserId: assigneeUserId, RequesterContactId: requesterContactId,
                Search: search, OpenOnly: openOnly ?? false,
                CursorUpdatedUtc: cursorUpdatedUtc, CursorId: cursorId,
                Limit: limit ?? 50);
            // v0.0.5: operators always get full visibility. Scope switches
            // to Company/Own once the customer portal ships.
            var page = await repo.SearchAsync(q, VisibilityScope.All, null, null, ct);
            return Results.Ok(new
            {
                items = page.Items,
                nextCursor = page.NextCursorUpdatedUtc.HasValue && page.NextCursorId.HasValue
                    ? new { updatedUtc = page.NextCursorUpdatedUtc, id = page.NextCursorId }
                    : null,
            });
        }).WithName("ListTickets").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, ITicketRepository repo, CancellationToken ct) =>
        {
            var detail = await repo.GetByIdAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithName("GetTicket").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] CreateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Subject))
                return Results.BadRequest(new { error = "Subject is required." });
            if (req.RequesterContactId == Guid.Empty)
                return Results.BadRequest(new { error = "requesterContactId is required." });

            var requester = await companies.GetContactAsync(req.RequesterContactId, ct);
            if (requester is null) return Results.BadRequest(new { error = "Unknown requester contact." });

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
                Source: req.Source ?? "Api"), ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.created",
                Actor: actor,
                ActorRole: role,
                Target: created.Id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { created.Number, created.Subject }));

            return Results.Created($"/api/tickets/{created.Id}", created);
        }).WithName("CreateTicket").WithOpenApi();

        group.MapPatch("/{id:guid}", async (
            Guid id, [FromBody] UpdateTicketRequest req, HttpContext http,
            ITicketRepository tickets, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
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

            return Results.Ok(detail);
        }).WithName("UpdateTicket").WithOpenApi();

        group.MapPost("/{id:guid}/events", async (
            Guid id, [FromBody] AddEventRequest req, HttpContext http,
            ITicketRepository tickets, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.EventType))
                return Results.BadRequest(new { error = "eventType is required." });
            if (req.EventType != "Comment" && req.EventType != "Note")
                return Results.BadRequest(new { error = "eventType must be 'Comment' or 'Note'." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
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

            return Results.Created($"/api/tickets/{id}/events/{evt.Id}", evt);
        }).WithName("AddTicketEvent").WithOpenApi();

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
            IPasswordHasher hasher,
            Npgsql.NpgsqlDataSource dataSource,
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
            return id != Guid.Empty
                ? Results.Ok(new { id, email, role = "Agent" })
                : Results.Conflict(new { error = "User already exists" });
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
}
