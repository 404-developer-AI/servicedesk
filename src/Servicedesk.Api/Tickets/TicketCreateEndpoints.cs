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
    public static IEndpointRouteBuilder MapTicketCreateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapPost("/", async (
            [FromBody] CreateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IContactLookupService contactLookup,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla,
            ITriggerService triggers, ISettingsService settings, IUserService users, IQueueStatusScope statusScope,
            Servicedesk.Infrastructure.Tickets.ITicketHtmlSanitizer htmlSanitizer, TimeProvider clock, CancellationToken ct) =>
        {
            req = req with
            {
                BodyHtml = htmlSanitizer.Sanitize(req.BodyHtml),
                InitialNote = req.InitialNote is { } initialNote
                    ? initialNote with { BodyHtml = htmlSanitizer.Sanitize(initialNote.BodyHtml) ?? string.Empty }
                    : null,
            };
            if (string.IsNullOrWhiteSpace(req.Subject))
                return Results.BadRequest(new { error = "Subject is required." });
            if (req.RequesterContactId == Guid.Empty)
                return Results.BadRequest(new { error = "requesterContactId is required." });
            // v0.1.44 — same rule as the side panel: never a past reminder.
            // Checked before any write (company link learning below).
            if (PendingTillRules.IsInPast(req.PendingTillUtc, clock.GetUtcNow().UtcDateTime))
                return Results.BadRequest(new { error = PendingTillRules.PastMessage, code = PendingTillRules.PastCode });
            if (req.PendingTillUtc.HasValue && !await statusScope.IsPendingStatusAsync(req.StatusId, ct))
                return Results.BadRequest(new { error = PendingTillRules.NotPendingMessage, code = PendingTillRules.NotPendingCode });
            if (req.AssigneeUserId is Guid assignee && !await TicketAssigneeRule.IsAssignableAsync(users, assignee, ct))
                return Results.BadRequest(new { error = TicketAssigneeRule.Message, code = TicketAssigneeRule.Code });

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // v0.0.105 — project tickets are pinned to the configured project
            // queue: a project create lands there regardless of the queue the
            // client sent (the drawer hides the queue field in that case).
            var effectiveQueueId = req.QueueId;
            if (req.IsProject && await ProjectQueuePin.GetPinnedQueueIdAsync(settings, ct) is Guid pinnedQueueId)
                effectiveQueueId = pinnedQueueId;

            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, effectiveQueueId, ct))
                return Results.Json(new { error = "You do not have access to this queue." }, statusCode: 403);

            var requester = await companies.GetContactAsync(req.RequesterContactId, ct);
            if (requester is null) return Results.BadRequest(new { error = "Unknown requester contact." });

            // Optional parent-link: the "Create linked ticket" flow on a
            // source ticket passes its own id here so the new ticket lands
            // as a sub-ticket. Validate access + non-merged + non-self
            // before insert; the link is applied in a follow-up call once
            // the new ticket exists.
            if (req.ParentTicketId is Guid parentId)
            {
                if (parentId == Guid.Empty)
                    return Results.BadRequest(new { error = "parentTicketId is empty." });
                // A parent in a queue the caller can't see answers exactly
                // like a missing one, so an id probe learns nothing.
                var parent = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, parentId, ct);
                if (parent is null)
                    return Results.BadRequest(new { error = "Parent ticket not found." });
                if (parent.MergedIntoTicketId is not null)
                    return Results.Conflict(new { error = "Parent ticket is merged.", code = "parent_is_merged" });
            }

            // v0.0.51 — when the UI passed an explicit CompanyId the
            // agent already made the decision in the create-time popup,
            // so the cascade is skipped. Mail-intake leaves CompanyId
            // null and falls through to the same ResolveCompanyForNewTicketAsync
            // path it has always used. NewLinkRole carries the learn-
            // flow choice (primary/secondary/supplier) for picks that
            // aren't yet on the contact's link list.
            Guid? resolvedCompanyId;
            bool awaitingCompany;
            string? resolvedVia;
            if (req.CompanyId is Guid pickedCompanyId)
            {
                var linkResult = await ApplyTicketCompanyChoiceAsync(
                    companies, req.RequesterContactId, pickedCompanyId, req.NewLinkRole, ct);
                if (linkResult.ErrorResult is not null) return linkResult.ErrorResult;

                resolvedCompanyId = pickedCompanyId;
                awaitingCompany = false;
                resolvedVia = "manual";
            }
            else
            {
                var resolution = await contactLookup.ResolveCompanyForNewTicketAsync(req.RequesterContactId, ct);
                resolvedCompanyId = resolution.CompanyId;
                awaitingCompany = resolution.Awaiting;
                resolvedVia = resolution.ResolvedVia;
            }

            var pendingTillUtc = req.PendingTillUtc;

            var created = await tickets.CreateAsync(new NewTicket(
                Subject: req.Subject.Trim(),
                BodyText: req.BodyText ?? "",
                BodyHtml: req.BodyHtml,
                RequesterContactId: req.RequesterContactId,
                QueueId: effectiveQueueId,
                StatusId: req.StatusId,
                PriorityId: req.PriorityId,
                CategoryId: req.CategoryId,
                AssigneeUserId: req.AssigneeUserId,
                Source: req.Source ?? "Api",
                CompanyId: resolvedCompanyId,
                AwaitingCompanyAssignment: awaitingCompany,
                CompanyResolvedVia: resolvedVia,
                PendingTillUtc: pendingTillUtc,
                TicketTypeId: req.TicketTypeId,
                InitialNote: req.InitialNote is { } note && !string.IsNullOrWhiteSpace(note.BodyHtml)
                    ? new InitialTicketNote(note.BodyHtml, note.IsInternal)
                    : null,
                IsProject: req.IsProject,
                IsCallback: req.IsCallback), ct);

            // Apply the parent link after create so the new ticket exists
            // for the FK + cycle-check. Failure here is logged but does not
            // roll the new ticket back — the agent can re-link from the
            // side panel afterwards if validation rejected.
            if (req.ParentTicketId is Guid parentTicketId)
            {
                var linkResult = await tickets.LinkParentAsync(created.Id, parentTicketId, userId, ct);
                if (!linkResult.Success)
                {
                    return Results.Conflict(new
                    {
                        error = $"Ticket was created but linking to parent failed: {linkResult.FailureReason}",
                        code = "parent_link_failed",
                        ticketId = created.Id,
                    });
                }
            }

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
            using var origin = Servicedesk.Infrastructure.Triggers.TriggerOrigin.Agent(userId);
            await triggers.EvaluateAsync(
                ticketId: created.Id,
                ticketEventId: null,
                activatorKind: TriggerActivatorKind.Action,
                changeSet: TriggerChangeSet.AllFieldsNew(),
                ct: ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, created.CompanyId, created.RequesterContactId, ct);
            var showAlertOnCreate = companyAlert is not null && companyAlert.AlertOnCreate
                && !string.IsNullOrWhiteSpace(companyAlert.AlertText);
            return Results.Created($"/api/tickets/{created.Id}", new
            {
                ticket = created,
                companyAlert,
                showAlertOnCreate,
            });
        }).WithName("CreateTicket");

        return app;
    }
}
