using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Campaigns;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Maintenance;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Api.Maintenance;

public sealed record MaintenanceNoteRequest(string? BodyHtml);

/// v0.1.67 — maintenance notes per plan member (ARCHITECTURE.md → Maintenance
/// planner → Notes). Two doors onto the same notes:
/// <list type="bullet">
/// <item><c>/api/tickets/{ticketId}/maintenance-notes</c> — any agent with
/// access to that round ticket (owner decision: the technician needs no
/// Campaigns access to add to the notes during the intervention). A write is
/// recorded against that ticket.</item>
/// <item><c>/api/maintenance/members/{memberId}/notes</c> — the planner, same
/// access boundary as the plan (RequireAgent + the Campaigns flag).</item>
/// </list>
/// Internal only: never in mail, the portal or the answer page. Ticket links
/// inside the notes are masked for tickets outside the caller's queues.
public static class MaintenanceNoteEndpoints
{
    public const int MaxBodyLength = 50_000;

    public static IEndpointRouteBuilder MapMaintenanceNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var ticket = app.MapGroup("/api/tickets/{ticketId:guid}/maintenance-notes")
            .WithTags("MaintenanceNotes")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        ticket.MapGet("", async (Guid ticketId, HttpContext http, NoteDeps d, CancellationToken ct) =>
        {
            if (await d.SubjectForTicketAsync(http, ticketId, ct) is not { } s) return Results.Ok(new { subject = (object?)null });
            return Results.Ok(await d.ViewAsync(http, s, ct));
        }).WithName("GetTicketMaintenanceNotes");

        ticket.MapGet("/history", async (Guid ticketId, HttpContext http, NoteDeps d, CancellationToken ct) =>
            await d.SubjectForTicketAsync(http, ticketId, ct) is { } s
                ? Results.Ok(await d.HistoryAsync(http, s, ct)) : Results.NotFound())
            .WithName("GetTicketMaintenanceNoteHistory");

        ticket.MapPost("", async (Guid ticketId, [FromBody] MaintenanceNoteRequest req, HttpContext http, NoteDeps d, CancellationToken ct) =>
            await d.SubjectForTicketAsync(http, ticketId, ct) is { } s
                ? await d.AddAsync(http, s, req, ticketId, ct) : Results.NotFound())
            .WithName("AddTicketMaintenanceNote");

        ticket.MapPut("/{noteId:guid}", async (Guid ticketId, Guid noteId, [FromBody] MaintenanceNoteRequest req, HttpContext http,
            NoteDeps d, CancellationToken ct) =>
            await d.SubjectForTicketAsync(http, ticketId, ct) is { } s
                ? await d.UpdateAsync(http, s, noteId, req, ticketId, ct) : Results.NotFound())
            .WithName("UpdateTicketMaintenanceNote");

        ticket.MapDelete("/{noteId:guid}", async (Guid ticketId, Guid noteId, HttpContext http, NoteDeps d, CancellationToken ct) =>
            await d.SubjectForTicketAsync(http, ticketId, ct) is { } s
                ? await d.DeleteAsync(http, s, noteId, ticketId, ct) : Results.NotFound())
            .WithName("DeleteTicketMaintenanceNote");

        var planner = app.MapGroup("/api/maintenance/members/{memberId:guid}/notes")
            .WithTags("MaintenanceNotes")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.Campaigns);

        planner.MapGet("", async (Guid memberId, HttpContext http, NoteDeps d, CancellationToken ct) =>
            await d.Notes.SubjectForMemberAsync(memberId, ct) is { } s ? Results.Ok(await d.ViewAsync(http, s, ct)) : Results.NotFound())
            .WithName("GetMemberMaintenanceNotes");

        planner.MapGet("/history", async (Guid memberId, HttpContext http, NoteDeps d, CancellationToken ct) =>
            await d.Notes.SubjectForMemberAsync(memberId, ct) is { } s ? Results.Ok(await d.HistoryAsync(http, s, ct)) : Results.NotFound())
            .WithName("GetMemberMaintenanceNoteHistory");

        planner.MapPost("", async (Guid memberId, [FromBody] MaintenanceNoteRequest req, HttpContext http, NoteDeps d, CancellationToken ct) =>
            await d.Notes.SubjectForMemberAsync(memberId, ct) is { } s ? await d.AddAsync(http, s, req, null, ct) : Results.NotFound())
            .WithName("AddMemberMaintenanceNote");

        planner.MapPut("/{noteId:guid}", async (Guid memberId, Guid noteId, [FromBody] MaintenanceNoteRequest req, HttpContext http,
            NoteDeps d, CancellationToken ct) =>
            await d.Notes.SubjectForMemberAsync(memberId, ct) is { } s ? await d.UpdateAsync(http, s, noteId, req, null, ct) : Results.NotFound())
            .WithName("UpdateMemberMaintenanceNote");

        planner.MapDelete("/{noteId:guid}", async (Guid memberId, Guid noteId, HttpContext http, NoteDeps d, CancellationToken ct) =>
            await d.Notes.SubjectForMemberAsync(memberId, ct) is { } s ? await d.DeleteAsync(http, s, noteId, null, ct) : Results.NotFound())
            .WithName("DeleteMemberMaintenanceNote");

        return app;
    }

    /// The shared handlers; bound per request.
    public sealed record NoteDeps(IMaintenanceNoteRepository Notes, ITicketRepository Tickets, IQueueAccessService QueueAccess,
        ITicketHtmlSanitizer Sanitizer, IAuditLogger Audit)
    {
        public static ValueTask<NoteDeps?> BindAsync(HttpContext http) => ValueTask.FromResult<NoteDeps?>(new NoteDeps(
            http.RequestServices.GetRequiredService<IMaintenanceNoteRepository>(),
            http.RequestServices.GetRequiredService<ITicketRepository>(),
            http.RequestServices.GetRequiredService<IQueueAccessService>(),
            http.RequestServices.GetRequiredService<ITicketHtmlSanitizer>(),
            http.RequestServices.GetRequiredService<IAuditLogger>()));

        /// The ticket must be one the caller may open (404 otherwise, never 403).
        public async Task<MaintenanceNoteSubject?> SubjectForTicketAsync(HttpContext http, Guid ticketId, CancellationToken ct)
        {
            var ticket = await Tickets.GetAccessibleCoreAsync(QueueAccess, ActorContext.GetUserId(http),
                ActorContext.GetRole(http.User), ticketId, ct);
            return ticket is null ? null : await Notes.SubjectForTicketAsync(ticketId, ct);
        }

        private Task<QueueAccessScope> ScopeAsync(HttpContext http, CancellationToken ct) =>
            QueueAccess.GetScopeAsync(ActorContext.GetUserId(http), ActorContext.GetRole(http.User), ct);

        public async Task<object> ViewAsync(HttpContext http, MaintenanceNoteSubject s, CancellationToken ct) => new
        {
            subject = new { s.MemberId, s.PlanId, s.PlanName, s.CompanyName, s.ContactName, s.Label },
            notes = (await Notes.ListAsync(s.MemberId, await ScopeAsync(http, ct), ct)).Select(n => new
            {
                n.Id, n.BodyHtml, n.TicketId, n.TicketNumber, n.CreatedByEmail, n.CreatedUtc, n.UpdatedByEmail, n.UpdatedUtc,
            }),
        };

        public async Task<object> HistoryAsync(HttpContext http, MaintenanceNoteSubject s, CancellationToken ct) => new
        {
            items = (await Notes.HistoryAsync(s.MemberId, await ScopeAsync(http, ct), ct)).Select(r => new
            {
                r.NoteId, r.Action, r.BodyHtml, r.TicketNumber, r.ByEmail, r.CreatedUtc,
            }),
        };

        public async Task<IResult> AddAsync(HttpContext http, MaintenanceNoteSubject s, MaintenanceNoteRequest req, Guid? ticketId, CancellationToken ct)
        {
            if (Clean(req) is not { } body) return Invalid();
            var id = await Notes.AddAsync(s.MemberId, body, ticketId, ActorContext.GetUserId(http), ct);
            await AuditAsync(http, "maintenance.note.added", s, id, ticketId, ct);
            return Results.Ok(new { id });
        }

        public async Task<IResult> UpdateAsync(HttpContext http, MaintenanceNoteSubject s, Guid noteId, MaintenanceNoteRequest req,
            Guid? ticketId, CancellationToken ct)
        {
            if (Clean(req) is not { } body) return Invalid();
            if (!await Notes.UpdateAsync(s.MemberId, noteId, body, ticketId, ActorContext.GetUserId(http), ct)) return Results.NotFound();
            await AuditAsync(http, "maintenance.note.edited", s, noteId, ticketId, ct);
            return Results.NoContent();
        }

        public async Task<IResult> DeleteAsync(HttpContext http, MaintenanceNoteSubject s, Guid noteId, Guid? ticketId, CancellationToken ct)
        {
            if (!await Notes.DeleteAsync(s.MemberId, noteId, ticketId, ActorContext.GetUserId(http), ct)) return Results.NotFound();
            await AuditAsync(http, "maintenance.note.deleted", s, noteId, ticketId, ct);
            return Results.NoContent();
        }

        private string? Clean(MaintenanceNoteRequest req)
        {
            var raw = req.BodyHtml ?? string.Empty;
            if (raw.Length > MaxBodyLength) return null;
            var html = Sanitizer.Sanitize(raw) ?? string.Empty;
            var text = global::System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ").Trim();
            return text.Length == 0 && !html.Contains("<img", StringComparison.OrdinalIgnoreCase) ? null : html;
        }

        private static IResult Invalid() =>
            Results.BadRequest(new { error = $"Write a note (at most {MaxBodyLength} characters)." });

        private Task AuditAsync(HttpContext http, string type, MaintenanceNoteSubject s, Guid noteId, Guid? ticketId, CancellationToken ct) =>
            CampaignEndpoints.AuditAsync(Audit, http, type, s.PlanId, new { memberId = s.MemberId, noteId, ticketId }, ct);
    }
}
