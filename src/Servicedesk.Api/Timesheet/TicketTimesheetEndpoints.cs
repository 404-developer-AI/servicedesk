using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// v0.0.35-F — ticket-scoped Timesheet reads for the expand-panel and the
/// "Import registered time" button on the reply editor. Every endpoint is
/// gated by <c>RequireAgent</c> plus a queue-access check on the ticket
/// (v0.1.2, audit v0.1.1 #5): time registrations carry agent identities and
/// the alert endpoints mutate the ticket, so they follow the same rule as
/// every other ticket-scoped surface — no access answers 404, never 403, so
/// ticket existence does not leak.
public static class TicketTimesheetEndpoints
{
    public static IEndpointRouteBuilder MapTicketTimesheetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/ticket")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/{ticketId:guid}", async (
            Guid ticketId,
            HttpContext http,
            ITicketRepository tickets,
            IQueueAccessService queueAccess,
            ITicketTimesheetService svc,
            CancellationToken ct) =>
        {
            if (!await HasTicketAccessAsync(ticketId, http, tickets, queueAccess, ct)) return Results.NotFound();
            var rows = await svc.ListByTicketAsync(ticketId, ct);
            var totalMinutes = rows.Sum(r => r.Minutes);
            return Results.Ok(new { items = rows, totalMinutes });
        })
        .WithName("ListTicketTimesheetEntries")
        .WithOpenApi();

        group.MapGet("/{ticketId:guid}/reply-html", async (
            Guid ticketId,
            HttpContext http,
            ITicketRepository tickets,
            IQueueAccessService queueAccess,
            ITicketTimesheetService svc,
            CancellationToken ct) =>
        {
            if (!await HasTicketAccessAsync(ticketId, http, tickets, queueAccess, ct)) return Results.NotFound();
            var html = await svc.BuildReplyHtmlAsync(ticketId, ct);
            return Results.Ok(new { html });
        })
        .WithName("GetTicketTimesheetReplyHtml")
        .WithOpenApi();

        // v0.0.87 — per-ticket hour-limit alert. Status drives both the
        // ticket-open warning popup and the "Time logged" remaining display.
        group.MapGet("/{ticketId:guid}/time-alert", async (
            Guid ticketId,
            HttpContext http,
            ITicketRepository tickets,
            IQueueAccessService queueAccess,
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
            if (!await HasTicketAccessAsync(ticketId, http, tickets, queueAccess, ct)) return Results.NotFound();
            var status = await svc.GetStatusAsync(ticketId, ct);
            return Results.Ok(status);
        })
        .WithName("GetTicketTimeAlertStatus")
        .WithOpenApi();

        // Agent dismissed the warning: logged, limit unchanged, recurs on
        // the next open while still over limit. v0.0.89 — admins may pass
        // ?silent=true to dismiss WITHOUT writing a timeline event. That is
        // authorised here, never trusted from the client: a non-admin's
        // silent=true is downgraded to a normal, logged dismissal.
        group.MapPost("/{ticketId:guid}/time-alert/dismiss", async (
            Guid ticketId,
            bool? silent,
            HttpContext http,
            ITicketRepository tickets,
            IQueueAccessService queueAccess,
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
            if (!await HasTicketAccessAsync(ticketId, http, tickets, queueAccess, ct)) return Results.NotFound();
            var (_, role) = ActorContext.Resolve(http);
            var effectiveSilent = silent == true && string.Equals(role, "Admin", StringComparison.Ordinal);
            await svc.DismissAsync(ticketId, ActorContext.GetUserId(http), effectiveSilent, ct);
            return Results.NoContent();
        })
        .WithName("DismissTicketTimeAlert")
        .WithOpenApi();

        // Agent raised the ticket's limit. The mandatory customer-confirmation
        // tick is enforced server-side, not just in the dialog.
        group.MapPost("/{ticketId:guid}/time-alert/extend", async (
            Guid ticketId,
            [FromBody] ExtendTimeAlertRequest req,
            HttpContext http,
            ITicketRepository tickets,
            IQueueAccessService queueAccess,
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
            if (!await HasTicketAccessAsync(ticketId, http, tickets, queueAccess, ct)) return Results.NotFound();
            var result = await svc.ExtendAsync(
                ticketId, ActorContext.GetUserId(http),
                req.AddMinutes, req.CustomerConfirmed, req.Note, ct);
            return result switch
            {
                TicketTimeAlertExtendResult.Ok => Results.NoContent(),
                TicketTimeAlertExtendResult.NotConfirmed =>
                    Results.Problem("Customer confirmation is required before raising the limit.", statusCode: 422),
                TicketTimeAlertExtendResult.InvalidMinutes =>
                    Results.Problem("The number of minutes to add is invalid.", statusCode: 400),
                TicketTimeAlertExtendResult.TicketNotFound => Results.NotFound(),
                _ => Results.Problem("Could not raise the ticket limit.", statusCode: 409),
            };
        })
        .WithName("ExtendTicketTimeAlert")
        .WithOpenApi();

        // v0.0.88 — agent disabled hour tracking for this ticket. The reason is
        // mandatory (re-checked server-side), posted as an internal note, and a
        // TimeLimitTrackingDisabled event is logged. One-way from the UI.
        group.MapPost("/{ticketId:guid}/time-alert/disable", async (
            Guid ticketId,
            [FromBody] DisableTimeAlertRequest req,
            HttpContext http,
            ITicketRepository tickets,
            IQueueAccessService queueAccess,
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
            if (!await HasTicketAccessAsync(ticketId, http, tickets, queueAccess, ct)) return Results.NotFound();
            var result = await svc.DisableAsync(
                ticketId, ActorContext.GetUserId(http), req.Reason ?? string.Empty, ct);
            return result switch
            {
                TicketTimeAlertDisableResult.Ok => Results.NoContent(),
                TicketTimeAlertDisableResult.ReasonRequired =>
                    Results.Problem("A reason is required to disable hour tracking.", statusCode: 422),
                TicketTimeAlertDisableResult.TicketNotFound => Results.NotFound(),
                _ => Results.Problem("Could not disable hour tracking.", statusCode: 409),
            };
        })
        .WithName("DisableTicketTimeAlert")
        .WithOpenApi();

        return app;
    }

    /// The same precheck every other ticket-scoped endpoint runs: resolve
    /// the ticket, check queue access for the caller, answer false (→ 404)
    /// on a miss so ticket existence never leaks across queue boundaries.
    private static async Task<bool> HasTicketAccessAsync(
        Guid ticketId,
        HttpContext http,
        ITicketRepository tickets,
        IQueueAccessService queueAccess,
        CancellationToken ct)
    {
        var ticket = await tickets.GetByIdAsync(ticketId, ct);
        if (ticket is null) return false;
        var userId = ActorContext.GetUserId(http);
        var (_, role) = ActorContext.Resolve(http);
        return await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct);
    }

    /// Body of the "allow more time" action. The optional note is posted as an
    /// internal note on the ticket (v0.0.88).
    public sealed record ExtendTimeAlertRequest(int AddMinutes, bool CustomerConfirmed, string? Note);

    /// Body of the "disable hour tracking" action (v0.0.88). The reason is
    /// mandatory and posted as an internal note.
    public sealed record DisableTimeAlertRequest(string? Reason);
}
