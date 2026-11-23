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
    public static IEndpointRouteBuilder MapTicketGateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // v0.0.42 — gate matching probe. Returns the active
        // gate:status_change triggers whose conditions + source/target
        // pair match a proposed status change. The FE calls this BEFORE
        // sending PATCH so it can open the confirmation dialog only when
        // a gate actually applies. PATCH re-evaluates the same matcher
        // server-side; this endpoint exists for UX only — the FE could
        // in principle skip it and let PATCH return 409 + the gates list,
        // but the round-trip is cheap and avoids a flash of stale UI.
        group.MapGet("/{id:guid}/status-gates", async (
            Guid id, Guid toStatusId, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IStatusGateService gates, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (current is null) return Results.NotFound();

            var matches = await gates.FindMatchingAsync(id, toStatusId, ct);
            return Results.Ok(new
            {
                items = matches.Select(g => ProjectGateForApi(g)),
            });
        }).WithName("ListStatusGates");

        // First-open title-review gate probe. The ticket detail page calls
        // this once on load; a non-null `gate` means the agent must review
        // the title in a blocking dialog before working the ticket. Returns
        // null when the ticket was already reviewed or no gate matches —
        // either way the page proceeds normally. Queue-access gated like
        // every other ticket sub-resource.
        group.MapGet("/{id:guid}/open-gates", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IFirstOpenGateService gates, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (current is null) return Results.NotFound();

            var gate = await gates.FindMatchingAsync(id, ct);
            return Results.Ok(new { gate = gate is null ? null : ProjectFirstOpenGateForApi(gate, id) });
        }).WithName("ListOpenGates");

        // Confirm a first-open title-review gate. Applies the (possibly
        // edited) subject, marks the ticket reviewed exactly once, and runs
        // the trigger's remaining actions (e.g. the approval note) so they
        // see the new subject + the confirming agent. Idempotent + race-
        // safe: if another agent already confirmed, this returns the
        // refreshed detail with reviewed=true and changes nothing.
        group.MapPost("/{id:guid}/open-gates/confirm", async (
            Guid id, [FromBody] ConfirmOpenGateRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla,
            IFirstOpenGateService gates, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetAccessibleDetailAsync(queueAccess, userId, userRole, id, ct);
            if (current is null) return Results.NotFound();

            var newSubject = req.Subject?.Trim();
            if (string.IsNullOrWhiteSpace(newSubject))
                return Results.BadRequest(new { error = "Subject is required.", code = "subject_required" });
            if (newSubject.Length > 500)
                return Results.BadRequest(new { error = "Subject must be 500 characters or fewer.", code = "subject_too_long" });

            var (actor, role) = ActorContext.Resolve(http);

            // Re-evaluate the gate server-side: this enforces that the
            // ticket still matches, isn't already reviewed, and that the
            // submitted trigger id is the one that matched (no client-
            // supplied trigger smuggling).
            var gate = await gates.FindMatchingAsync(id, ct);
            if (gate is null)
            {
                // Already reviewed or no longer matching — idempotent close.
                var done = await tickets.GetByIdAsync(id, ct) ?? current;
                return Results.Ok(new
                {
                    reviewed = true,
                    ticket = done.Ticket,
                    body = done.Body,
                    pinnedEvents = done.PinnedEvents,
                });
            }
            if (gate.TriggerId != req.TriggerId)
            {
                return Results.Conflict(new
                {
                    error = "The open-gate prompt changed; re-open the dialog.",
                    code = "open_gate_mismatch",
                    gate = ProjectFirstOpenGateForApi(gate, id),
                });
            }

            var previousSubject = current.Ticket.Subject;

            // Atomic one-time claim. The losing side of a concurrent
            // confirmation gets reviewed=true without re-applying the
            // subject or re-running the actions (no double note).
            var claimed = await tickets.MarkTitleReviewedAsync(id, userId, ct);
            if (!claimed)
            {
                var raced = await tickets.GetByIdAsync(id, ct) ?? current;
                return Results.Ok(new
                {
                    reviewed = true,
                    ticket = raced.Ticket,
                    body = raced.Body,
                    pinnedEvents = raced.PinnedEvents,
                });
            }

            // Apply the (possibly edited) subject before running the
            // actions so the note template's #{ticket.subject} resolves to
            // the new value.
            var detail = await tickets.UpdateFieldsAsync(id, new TicketFieldUpdate(Subject: newSubject), userId, ct);
            if (detail is null) return Results.NotFound();

            // Run the gate trigger's remaining actions (approval note, …).
            // Best-effort inside the service — never throws.
            await gates.RunConfirmActionsAsync(req.TriggerId, id, userId, previousSubject, ct);

            // Refresh so the response carries any note the actions appended.
            detail = await tickets.GetByIdAsync(id, ct) ?? detail;

            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.title_reviewed",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { triggerId = req.TriggerId, previousSubject, newSubject }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);
            await sla.OnTicketFieldsChangedAsync(id, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.CompanyId, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                reviewed = true,
                ticket = detail.Ticket,
                body = detail.Body,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("ConfirmOpenGate");

        return app;
    }
}
