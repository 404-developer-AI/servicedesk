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
    public static IEndpointRouteBuilder MapTicketUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // v0.0.102 — bulk action: post a note/comment and/or change status,
        // queue, assignee, priority on many tickets in one request. Each
        // ticket runs the full single-ticket rule set through the shared
        // mutation service (queue access, status-in-queue, status gates,
        // triggers, SLA, audit, realtime); tickets that fail a rule are
        // skipped and reported, the batch never stops on one ticket. The
        // "bulk" rate-limit policy bounds abuse; the max-selection setting
        // bounds one request's work.
        group.MapPost("/bulk", async (
            [FromBody] BulkTicketActionRequest req, HttpContext http,
            ITicketBulkActionService bulk, CancellationToken ct) =>
        {
            if (req.TicketIds is null || req.TicketIds.Count == 0)
                return Results.BadRequest(new { error = "ticketIds is required.", code = "no_tickets" });
            if (req.TicketIds.Count > TicketBulkActionService.HardMaxSelection)
                return Results.BadRequest(new
                {
                    error = $"A bulk action may touch at most {TicketBulkActionService.HardMaxSelection} tickets.",
                    code = "too_many",
                });
            if (req.MessageHtml is { Length: > 200_000 })
                return Results.BadRequest(new { error = "Message is too long.", code = "message_too_long" });

            var actor = ResolveMutationActor(http);
            try
            {
                var result = await bulk.ExecuteAsync(actor, new TicketBulkActionRequest(
                    TicketIds: req.TicketIds,
                    MessageHtml: string.IsNullOrWhiteSpace(req.MessageHtml) ? null : req.MessageHtml,
                    MessageIsInternal: req.MessageIsInternal ?? true,
                    StatusId: req.StatusId,
                    QueueId: req.QueueId,
                    PriorityId: req.PriorityId,
                    AssigneeUserId: req.AssigneeUserId,
                    UnassignAssignee: req.UnassignAssignee,
                    PendingTillUtc: req.PendingTillUtc), ct);
                return Results.Ok(result);
            }
            catch (TicketBulkActionRejectedException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.Code });
            }
        }).WithName("BulkTicketAction").RequireRateLimiting("bulk");

        group.MapPatch("/{id:guid}", async (
            Guid id, [FromBody] UpdateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies,
            ITicketMutationService mutations, IStatusGateConfirmationService gateConfirmations,
            Servicedesk.Infrastructure.Persistence.Taxonomy.ITaxonomyRepository taxonomy, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            if (req.ClearCategory && req.CategoryId.HasValue)
                return Results.BadRequest(new { error = "Choose a category or clear it, not both.", code = "category_conflict" });
            if (req.CategoryId is Guid categoryId)
            {
                var category = await taxonomy.GetCategoryAsync(categoryId, ct);
                if (category is null || !category.IsActive)
                    return Results.BadRequest(new { error = "Unknown or inactive category.", code = "category_invalid" });
            }
            var actor = ResolveMutationActor(http);

            // v0.0.102 — the rule set (queue access on current + target
            // queue, status-in-queue-scope, status-gate probe) lives in the
            // shared mutation service so the bulk action runs the very same
            // checks per ticket. The endpoint only maps verdicts to HTTP.
            var requested = new TicketFieldUpdate(
                QueueId: req.QueueId,
                StatusId: req.StatusId,
                PriorityId: req.PriorityId,
                CategoryId: req.CategoryId,
                AssigneeUserId: req.AssigneeUserId,
                ClearAssignee: req.UnassignAssignee,
                Subject: req.Subject?.Trim(),
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml,
                PendingTillUtc: req.PendingTillUtc,
                IsCallback: req.IsCallback,
                IsResearch: req.IsResearch,
                DescriptionInternal: req.DescriptionInternal,
                ClearCategory: req.ClearCategory);
            var pre = await mutations.PrecheckFieldUpdateAsync(actor, id, requested, ct);
            switch (pre.Check)
            {
                case TicketMutationCheck.NotFound:
                case TicketMutationCheck.NoAccess:
                    return Results.NotFound();
                case TicketMutationCheck.TargetQueueNoAccess:
                    return Results.Json(new { error = "You do not have access to the target queue." }, statusCode: 403);
                case TicketMutationCheck.StatusNotInQueueScope:
                    // v0.0.40 polish — catches the rare case where a stale
                    // client form posts a status the dropdown filter would
                    // have hidden.
                    return Results.BadRequest(new
                    {
                        error = "This status is not allowed for the target queue.",
                        code = "status_not_in_queue_scope",
                    });
                case TicketMutationCheck.ProjectQueueLocked:
                    // v0.0.105 — project tickets are pinned to the project
                    // queue; the queue selector is hidden client-side, so
                    // this only catches stale clients.
                    return Results.Conflict(new
                    {
                        error = "Project tickets live in the project queue and cannot be moved to another queue.",
                        code = "project_queue_locked",
                    });
                case TicketMutationCheck.PendingTillInPast:
                    // v0.1.44 — the picker disables past moments, so this
                    // only catches a stale tab or a hand-rolled call.
                    return Results.BadRequest(new { error = PendingTillRules.PastMessage, code = PendingTillRules.PastCode });
                case TicketMutationCheck.AssigneeNotAssignable:
                    return Results.BadRequest(new { error = TicketAssigneeRule.Message, code = TicketAssigneeRule.Code });
                case TicketMutationCheck.ChecklistIncomplete:
                    // v0.0.103 — hard block: no confirmation payload can
                    // satisfy it. The body names the checklists so the FE
                    // can offer "Open checklist".
                    return Results.Json(new
                    {
                        error = "Finish the checklist before resolving or closing this ticket.",
                        code = "checklist_incomplete",
                        checklists = (pre.ChecklistBlockers ?? Array.Empty<Servicedesk.Infrastructure.Checklists.ChecklistBlocker>())
                            .Select(b => new { checklistId = b.ChecklistId, name = b.Name, openRequired = b.OpenRequired }),
                    }, statusCode: 409);
            }
            var current = pre.Ticket!;

            // v0.0.42 — status-change gates: only for a real status change on
            // the agent path (triggers, mail-ingest and bulk never negotiate).
            // Missing or incomplete confirmations yield 409 + the unsatisfied
            // gates so the FE can re-open the dialog without re-probing.
            var negotiation = new GateNegotiation(Array.Empty<MatchedStatusGate>(), Array.Empty<ConfirmedStatusGate>());
            if (req.StatusId.HasValue && req.StatusId.Value != current.StatusId && pre.Gates.Count > 0)
            {
                var confirmations = (req.GateConfirmations ?? Array.Empty<GateConfirmationInput>())
                    .Select(c => new GateConfirmation(c.TriggerId, c.Answers, c.CompanyId, c.Role))
                    .ToList();
                negotiation = await gateConfirmations.NegotiateAsync(pre.Gates, confirmations, ct);
                if (negotiation.InvalidCompany)
                    return Results.BadRequest(new { error = "Selected company is not available.", code = "invalid_company" });
                if (negotiation.Missing.Count > 0)
                {
                    return Results.Json(new
                    {
                        error = "Status change requires confirmation from one or more gates.",
                        code = "status_gate_required",
                        gates = negotiation.Missing.Select(g => ProjectGateForApi(g)),
                    }, statusCode: 409);
                }
                // A company-link gate's link is its precondition: applied in
                // the same request, before the status flip.
                await gateConfirmations.ApplyPendingLinksAsync(negotiation, ct);
            }

            // v0.0.89 — a keep_open choice holds the ticket in its current
            // status: the status change is dropped, other edits still apply.
            var update = negotiation.KeepOpen ? requested with { StatusId = null } : requested;
            var detail = await mutations.ApplyFieldUpdateAsync(id, update, userId, ct);
            if (detail is null) return Results.NotFound();

            // Confirmation notes + decision events after the write (they sort
            // after the StatusChange); refresh so the response carries them.
            if (await gateConfirmations.RecordAsync(id, userId, negotiation, ct))
                detail = await tickets.GetByIdAsync(id, ct) ?? detail;

            // Audit → realtime → SLA → triggers, shared with the bulk path.
            // The trigger ChangeSet is built from the raw request (any
            // non-null field counts as "changed", incl. a status the
            // keep-open gate just dropped) — same as before the extraction.
            await mutations.PublishFieldUpdateAsync(
                actor, id, update,
                auditPayload: req,
                changeSet: new TriggerChangeSet(
                    TicketMutationService.DeriveChangedFields(requested), ArticleAdded: false),
                ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.CompanyId, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("UpdateTicket");

        return app;
    }
}
