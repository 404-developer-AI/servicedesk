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
    public static IEndpointRouteBuilder MapTicketComposeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapPost("/{id:guid}/mail", async (
            Guid id, [FromBody] SendOutboundMailRequest req, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IOutboundMailService outbound, IHubContext<TicketPresenceHub> hub,
            IComposeTemplateSurveyDispatcher surveyTemplateDispatcher,
            IAuditLogger audit, ISettingsService settings, IWorkflowCaptureService workflow,
            Servicedesk.Infrastructure.Tickets.ITicketHtmlSanitizer htmlSanitizer,
            CancellationToken ct) =>
        {
            req = req with { BodyHtml = htmlSanitizer.Sanitize(req.BodyHtml) };
            var userId = ActorContext.GetUserId(http);
            var userRole = ActorContext.GetRole(http.User);

            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (ticket is null) return Results.NotFound();

            // v0.0.105 — project tickets are internal: no outbound mail from
            // a project. The composer hides the mail tab; this is the server
            // gate for stale clients. Only enforced while projects are on —
            // switching the feature off returns the ticket to normal rules.
            if (await ProjectMailGuard.IsBlockedAsync(settings, ticket.IsProject, ct))
                return Results.Conflict(new
                {
                    error = "This is a project ticket — outbound mail is disabled on projects. Post an internal note, or mail from a linked ticket instead.",
                    code = "project_ticket_mail_disabled",
                });

            if (!Enum.TryParse<OutboundMailKind>(req.Kind, ignoreCase: true, out var kind))
                return Results.BadRequest(new { error = "kind must be Reply, ReplyAll, or New." });

            var request = new OutboundMailRequest(
                TicketId: id,
                AuthorUserId: userId,
                Kind: kind,
                To: MapRecipients(req.To),
                Cc: MapRecipients(req.Cc),
                Bcc: MapRecipients(req.Bcc),
                Subject: req.Subject ?? string.Empty,
                BodyHtml: req.BodyHtml ?? string.Empty,
                AttachmentIds: req.AttachmentIds,
                MentionedUserIds: req.MentionedUserIds,
                LinkedFormIds: req.LinkedFormIds,
                MentionedMailboxIds: req.MentionedMailboxIds,
                MentionedGroupIds: req.MentionedGroupIds,
                SignaturePreloaded: req.SignaturePreloaded,
                AuthorRole: userRole);

            // v0.1.32 — Insights Workflow pickup, before the send moves anything.
            await workflow.RecordPickupAsync(userId, id, "Mail", ct);
            var result = await outbound.SendAsync(request, ct);
            switch (result.Status)
            {
                case OutboundMailStatus.TicketNotFound:
                    return Results.NotFound();
                case OutboundMailStatus.NoMailboxConfigured:
                    return Results.BadRequest(new { error = result.ErrorMessage });
                case OutboundMailStatus.InvalidRequest:
                    return Results.BadRequest(new { error = result.ErrorMessage });
                case OutboundMailStatus.AttachmentTooLarge:
                    return Results.Json(new { error = result.ErrorMessage }, statusCode: 413);
            }

            var evt = result.Event!;
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.mail.sent",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    kind = kind.ToString(),
                    toCount = request.To.Count,
                    ccCount = request.Cc.Count,
                    bccCount = request.Bcc.Count,
                    subject = request.Subject,
                    attachmentCount = req.AttachmentIds?.Count ?? 0,
                    mentionedUserCount = result.MentionedUserCount,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            // v0.0.38 — fire any compose-template-linked surveys after the
            // mail-send succeeds. Failures inside the dispatcher are logged
            // there; we never want a survey-mail hiccup to roll back the
            // already-sent reply.
            if (req.ComposeTemplateIds is { Count: > 0 } tplIds)
            {
                await surveyTemplateDispatcher.DispatchForTemplatesAsync(id, tplIds, userId, ct);
            }

            return Results.Created($"/api/tickets/{id}/events/{evt.Id}", evt);
        }).WithName("SendTicketMail");

        // v0.0.61 — resolved signature preview for the compose window. Returns a
        // self-contained (data-URI) HTML block to pre-load directly under the
        // agent's message, or { html: null } when nothing should be pre-loaded
        // (feature off, preload off, reply suppressed, or no signature bound to
        // the queue). The send path renders the authoritative cid version.
        group.MapGet("/{id:guid}/compose-signature", async (
            Guid id, bool? reply, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            ISettingsService settings, ISignatureComposer signatures, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = ActorContext.GetRole(http.User);

            var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (ticket is null) return Results.NotFound();

            if (!await settings.GetAsync<bool>(SettingKeys.Signatures.ComposerPreload, ct))
                return Results.Ok(new { html = (string?)null });

            var html = await signatures.ComposePreviewForQueueAsync(
                ticket.QueueId, userId, isReply: reply ?? false, ct);
            return Results.Ok(new { html });
        }).WithName("GetComposeSignature");

        // Ranked recipient suggestions for the composer's To/Cc/Bcc fields:
        // company contacts + previously-used addresses first (most-used on
        // this company's outbound mail on top), general contact matches below
        // when a search term is given. Access = queue access on the ticket,
        // checked via a lightweight context fetch (no full detail load — this
        // fires on focus and per keystroke).
        group.MapGet("/{id:guid}/recipient-suggestions", async (
            Guid id, string? q, HttpContext http,
            IRecipientSuggestionRepository suggestions, IQueueAccessService queueAccess,
            CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var userRole = ActorContext.GetRole(http.User);

            var context = await suggestions.GetTicketContextAsync(id, ct);
            if (context is null) return Results.NotFound();
            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, context.QueueId, ct))
                return Results.NotFound();

            var items = await suggestions.ListAsync(context.CompanyId, q, limit: 20, ct);
            return Results.Ok(new { items });
        }).WithName("GetRecipientSuggestions");

        return app;
    }
}
