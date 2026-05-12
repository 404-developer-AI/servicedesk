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

        return app;
    }
}
