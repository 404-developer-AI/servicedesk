using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// v0.0.35-F — ticket-scoped Timesheet reads for the expand-panel and the
/// "Import registered time" button on the reply editor. Every endpoint is
/// gated by <c>RequireAgent</c>; customers have no surface here.
///
/// No write-paths live in this group — entries are still mutated through
/// the existing own-row endpoints (Tab 1) or manager endpoints (Tab 2).
public static class TicketTimesheetEndpoints
{
    public static IEndpointRouteBuilder MapTicketTimesheetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/ticket")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/{ticketId:guid}", async (
            Guid ticketId,
            ITicketTimesheetService svc,
            CancellationToken ct) =>
        {
            var rows = await svc.ListByTicketAsync(ticketId, ct);
            var totalMinutes = rows.Sum(r => r.Minutes);
            return Results.Ok(new { items = rows, totalMinutes });
        })
        .WithName("ListTicketTimesheetEntries")
        .WithOpenApi();

        group.MapGet("/{ticketId:guid}/reply-html", async (
            Guid ticketId,
            ITicketTimesheetService svc,
            CancellationToken ct) =>
        {
            var html = await svc.BuildReplyHtmlAsync(ticketId, ct);
            return Results.Ok(new { html });
        })
        .WithName("GetTicketTimesheetReplyHtml")
        .WithOpenApi();

        // v0.0.87 — per-ticket hour-limit alert. Status drives both the
        // ticket-open warning popup and the "Time logged" remaining display.
        group.MapGet("/{ticketId:guid}/time-alert", async (
            Guid ticketId,
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
            var status = await svc.GetStatusAsync(ticketId, ct);
            return Results.Ok(status);
        })
        .WithName("GetTicketTimeAlertStatus")
        .WithOpenApi();

        // Agent dismissed the warning: logged, limit unchanged, recurs on
        // the next open while still over limit.
        group.MapPost("/{ticketId:guid}/time-alert/dismiss", async (
            Guid ticketId,
            HttpContext http,
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
            await svc.DismissAsync(ticketId, ActorContext.GetUserId(http), ct);
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
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
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
            ITicketTimeAlertService svc,
            CancellationToken ct) =>
        {
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

    /// Body of the "allow more time" action. The optional note is posted as an
    /// internal note on the ticket (v0.0.88).
    public sealed record ExtendTimeAlertRequest(int AddMinutes, bool CustomerConfirmed, string? Note);

    /// Body of the "disable hour tracking" action (v0.0.88). The reason is
    /// mandatory and posted as an internal note.
    public sealed record DisableTimeAlertRequest(string? Reason);
}
