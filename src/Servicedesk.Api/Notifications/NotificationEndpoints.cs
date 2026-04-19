using System.Globalization;
using System.Security.Claims;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Notifications;

namespace Servicedesk.Api.Notifications;

/// v0.0.12 stap 4 — agent-facing endpoints for the @@-mention notification
/// raamwerk. Every route is scoped to the calling agent via
/// <see cref="ClaimTypes.NameIdentifier"/>; the repo methods re-check
/// ownership on every write so a guessed id from another session can't
/// mark an inbox-entry that isn't theirs.
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications")
            .WithTags("Notifications")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/pending", async (
            HttpContext http, INotificationRepository repo, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var rows = await repo.ListPendingForUserAsync(userId, ct);
            return Results.Ok(rows.Select(Map));
        }).WithName("ListPendingNotifications").WithOpenApi();

        group.MapGet("/history", async (
            HttpContext http, INotificationRepository repo,
            string? cursorUtc, Guid? cursorId, int? limit, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            NotificationHistoryCursor? cursor = null;
            if (!string.IsNullOrWhiteSpace(cursorUtc) && cursorId is not null
                && DateTime.TryParse(cursorUtc, null, DateTimeStyles.RoundtripKind, out var parsed))
            {
                cursor = new NotificationHistoryCursor(parsed, cursorId.Value);
            }

            var effectiveLimit = limit ?? 50;
            var rows = await repo.ListHistoryForUserAsync(userId, cursor, effectiveLimit, ct);
            var items = rows.Select(Map).ToList();
            // Next-cursor points at the last row of this page so the client
            // can request the next. Null when the page is under-filled —
            // signals "no more rows" to the frontend.
            NotificationHistoryCursorDto? nextCursor = null;
            if (items.Count == effectiveLimit)
            {
                var last = rows[^1];
                nextCursor = new NotificationHistoryCursorDto(
                    last.CreatedUtc.ToString("O"), last.Id);
            }
            return Results.Ok(new { items, nextCursor });
        }).WithName("ListNotificationHistory").WithOpenApi();

        group.MapPost("/{id:guid}/view", async (
            Guid id, HttpContext http, INotificationRepository repo, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var row = await repo.GetByIdForUserAsync(id, userId, ct);
            if (row is null) return Results.NotFound();

            var transitioned = await repo.MarkViewedAsync(id, userId, ct);
            if (transitioned)
            {
                var (actor, role) = ActorContext.Resolve(http);
                await audit.LogAsync(new AuditEvent(
                    EventType: "notification.viewed",
                    Actor: actor,
                    ActorRole: role,
                    Target: id.ToString(),
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new { row.TicketId, row.EventId, row.NotificationType }));
            }
            return Results.NoContent();
        }).WithName("MarkNotificationViewed").WithOpenApi();

        group.MapPost("/{id:guid}/ack", async (
            Guid id, HttpContext http, INotificationRepository repo, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var row = await repo.GetByIdForUserAsync(id, userId, ct);
            if (row is null) return Results.NotFound();

            var transitioned = await repo.MarkAckedAsync(id, userId, ct);
            if (transitioned)
            {
                var (actor, role) = ActorContext.Resolve(http);
                await audit.LogAsync(new AuditEvent(
                    EventType: "notification.acked",
                    Actor: actor,
                    ActorRole: role,
                    Target: id.ToString(),
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new { row.TicketId, row.EventId, row.NotificationType }));
            }
            return Results.NoContent();
        }).WithName("MarkNotificationAcked").WithOpenApi();

        group.MapPost("/ack-all", async (
            HttpContext http, INotificationRepository repo, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var count = await repo.MarkAllAckedAsync(userId, ct);
            if (count > 0)
            {
                var (actor, role) = ActorContext.Resolve(http);
                await audit.LogAsync(new AuditEvent(
                    EventType: "notification.acked",
                    Actor: actor,
                    ActorRole: role,
                    Target: "all",
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new { count }));
            }
            return Results.NoContent();
        }).WithName("AckAllNotifications").WithOpenApi();

        return app;
    }

    private static UserNotificationDto Map(UserNotificationRow r) => new(
        Id: r.Id,
        TicketId: r.TicketId,
        TicketNumber: r.TicketNumber,
        TicketSubject: r.TicketSubject,
        SourceUserId: r.SourceUserId,
        SourceUserEmail: r.SourceUserEmail,
        EventId: r.EventId,
        EventType: r.EventType,
        PreviewText: r.PreviewText,
        CreatedUtc: r.CreatedUtc,
        ViewedUtc: r.ViewedUtc,
        AckedUtc: r.AckedUtc);

    public sealed record UserNotificationDto(
        Guid Id,
        Guid TicketId,
        long TicketNumber,
        string TicketSubject,
        Guid? SourceUserId,
        string? SourceUserEmail,
        long EventId,
        string EventType,
        string PreviewText,
        DateTime CreatedUtc,
        DateTime? ViewedUtc,
        DateTime? AckedUtc);

    public sealed record NotificationHistoryCursorDto(string CreatedUtc, Guid Id);
}
