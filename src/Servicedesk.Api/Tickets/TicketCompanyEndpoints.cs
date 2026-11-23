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
    public static IEndpointRouteBuilder MapTicketCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

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

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (current is null) return Results.NotFound();

            // v0.0.51 — validate the company + handle the learn-flow link
            // role choice with the same rules the create-time POST uses.
            // Failure responses (400/409) come back before we touch the
            // ticket so a rejected role can't leave an orphan link behind.
            var linkChoice = await ApplyTicketCompanyChoiceAsync(
                companies, current.RequesterContactId, req.CompanyId, req.NewLinkRole, ct);
            if (linkChoice.ErrorResult is not null) return linkChoice.ErrorResult;

            var targetCompany = await companies.GetCompanyAsync(req.CompanyId, ct);
            // Helper already validated existence + active — re-fetch only
            // to surface the name in the audit payload below.
            if (targetCompany is null) return Results.BadRequest(new { error = "Unknown company." });

            // Infer the resolution reason from the contact's current link set.
            // supplier_only → contact has links but none are primary/secondary.
            // ambiguous_secondary → everything else that landed in awaiting
            // (multiple secondaries, mixed, etc.). Agents overriding an already
            // resolved ticket get reason='override'.
            string reason;
            if (current.AwaitingCompanyAssignment)
            {
                var links = await companies.ListContactLinksAsync(current.RequesterContactId, ct);
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
                    previousCompanyId = current.CompanyId,
                    reason,
                    newLinkRole = linkChoice.NewLinkCreated ? req.NewLinkRole : null,
                    contactId = current.RequesterContactId,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.CompanyId, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("AssignTicketCompany");

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

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (current is null) return Results.NotFound();

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
                    fromContactId = current.RequesterContactId,
                    toContactId = req.ContactId,
                    fromCompanyId = current.CompanyId,
                    toCompanyId = resolution.CompanyId,
                    resolvedVia = resolution.ResolvedVia,
                    awaiting = resolution.Awaiting,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            await sla.OnTicketFieldsChangedAsync(id, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.CompanyId, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("ChangeTicketRequester");

        return app;
    }
}
