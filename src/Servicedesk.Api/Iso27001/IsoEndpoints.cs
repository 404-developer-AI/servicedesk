using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Iso27001;

/// v0.0.40 — ISO 27001 workflow endpoints. MGM reviews a fresh ISO
/// ticket and either classifies it as a non-incident (closes the
/// ticket) or as an incident (parks it for the DPO in "Awaiting ISMS
/// registration"). The DPO closes the ticket from that bucket through
/// the regular status picker once the entry exists in the ISMS — there
/// is no dedicated endpoint for that step. Each transition demands a
/// non-empty motivation that lands as an internal note + a structured
/// audit row, so an external auditor can later trace every
/// classification with the actor's reasoning intact.
///
/// Both endpoints validate (in order): queue matches the
/// Iso27001.QueueId setting, current status matches the expected stage,
/// the caller has the MGM flag. Disabled state: Iso27001.QueueId empty
/// → every call returns 409 with code='workflow_disabled' so a stale
/// UI cache can't trigger ghost flips.
public static class IsoEndpoints
{
    public static IEndpointRouteBuilder MapIsoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("ISO 27001")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapPost("/{id:guid}/iso/classify-event", ClassifyEvent)
            .WithName("IsoClassifyEvent").WithOpenApi();
        group.MapPost("/{id:guid}/iso/classify-incident", ClassifyIncident)
            .WithName("IsoClassifyIncident").WithOpenApi();

        return app;
    }

    public sealed record IsoClassificationRequest(
        [property: Required] string Motivation);

    // ---- Public endpoints ------------------------------------------------

    private static Task<IResult> ClassifyEvent(
        Guid id, [FromBody] IsoClassificationRequest req, HttpContext http,
        ITicketRepository tickets, ITaxonomyRepository taxonomy,
        ISettingsService settings, IUserService users,
        IQueueAccessService queueAccess,
        IHubContext<TicketPresenceHub> hub, IAuditLogger audit,
        CancellationToken ct) =>
        PerformTransitionAsync(
            ticketId: id,
            motivation: req.Motivation,
            expectedFromSlug: IsoSlugs.MgmReview,
            targetSlug: IsoSlugs.NoIncident,
            flagCheck: f => f.Mgm,
            flagRoleLabel: "MGM",
            noteHeadingHtml: "<b>Classified as event</b> (no incident).",
            auditEventType: IsoAuditEventTypes.ClassifiedAsEvent,
            http, tickets, taxonomy, settings, users, queueAccess, hub, audit, ct);

    private static Task<IResult> ClassifyIncident(
        Guid id, [FromBody] IsoClassificationRequest req, HttpContext http,
        ITicketRepository tickets, ITaxonomyRepository taxonomy,
        ISettingsService settings, IUserService users,
        IQueueAccessService queueAccess,
        IHubContext<TicketPresenceHub> hub, IAuditLogger audit,
        CancellationToken ct) =>
        PerformTransitionAsync(
            ticketId: id,
            motivation: req.Motivation,
            expectedFromSlug: IsoSlugs.MgmReview,
            targetSlug: IsoSlugs.AwaitingIsms,
            flagCheck: f => f.Mgm,
            flagRoleLabel: "MGM",
            noteHeadingHtml: "<b>Classified as incident</b> — handover to DPO for ISMS registration.",
            auditEventType: IsoAuditEventTypes.ClassifiedAsIncident,
            http, tickets, taxonomy, settings, users, queueAccess, hub, audit, ct);

    // ---- Shared transition logic ----------------------------------------

    private static async Task<IResult> PerformTransitionAsync(
        Guid ticketId,
        string motivation,
        string expectedFromSlug,
        string targetSlug,
        Func<IsoFlags, bool> flagCheck,
        string flagRoleLabel,
        string noteHeadingHtml,
        string auditEventType,
        HttpContext http,
        ITicketRepository tickets,
        ITaxonomyRepository taxonomy,
        ISettingsService settings,
        IUserService users,
        IQueueAccessService queueAccess,
        IHubContext<TicketPresenceHub> hub,
        IAuditLogger audit,
        CancellationToken ct)
    {
        // 1. Motivation guard. Server-side check matches the FE modal's
        //    min-length so a tampered request can't slip an empty entry
        //    into the audit log.
        var trimmed = (motivation ?? string.Empty).Trim();
        if (trimmed.Length < 5)
            return Results.BadRequest(new { error = "Motivation must be at least 5 characters.", code = "motivation_required" });
        if (trimmed.Length > 4000)
            return Results.BadRequest(new { error = "Motivation must be 4000 characters or fewer.", code = "motivation_too_long" });

        // 2. Feature gate. The workflow is fully opt-in; an admin
        //    populates Iso27001.QueueId after creating the dedicated
        //    queue. Empty = the entire flow is dormant.
        var configuredQueueIdRaw = await settings.GetAsync<string>(SettingKeys.Iso27001.QueueId, ct);
        if (string.IsNullOrWhiteSpace(configuredQueueIdRaw) || !Guid.TryParse(configuredQueueIdRaw, out var configuredQueueId))
            return Results.Conflict(new { error = "ISO 27001 workflow is not configured.", code = "workflow_disabled" });

        // 3. User identity + ISO flag check.
        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;
        var flags = await users.GetIsoFlagsAsync(userId, ct);
        if (!flagCheck(flags))
            return Results.Json(
                new { error = $"You don't have the ISO 27001 {flagRoleLabel} role.", code = "missing_iso_role" },
                statusCode: 403);

        // 4. Load ticket + queue-access gate (same 404-leak pattern as
        //    the rest of the ticket endpoints — unauthorized callers get
        //    NotFound, never a hint that the ticket exists).
        var detail = await tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return Results.NotFound();
        if (!await queueAccess.HasQueueAccessAsync(userId, userRole, detail.Ticket.QueueId, ct))
            return Results.NotFound();

        // 5. Queue + status match the workflow's expected stage.
        if (detail.Ticket.QueueId != configuredQueueId)
            return Results.Conflict(new { error = "Ticket is not in the ISO 27001 queue.", code = "wrong_queue" });

        var statuses = await taxonomy.ListStatusesAsync(ct);
        var fromStatus = statuses.FirstOrDefault(s => string.Equals(s.Slug, expectedFromSlug, StringComparison.OrdinalIgnoreCase));
        var toStatus = statuses.FirstOrDefault(s => string.Equals(s.Slug, targetSlug, StringComparison.OrdinalIgnoreCase));
        if (fromStatus is null || toStatus is null)
            return Results.Conflict(new { error = "ISO 27001 statuses are missing from the taxonomy.", code = "statuses_missing" });
        if (detail.Ticket.StatusId != fromStatus.Id)
            return Results.Conflict(new
            {
                error = $"Expected status '{fromStatus.Name}', current status is different.",
                code = "wrong_status",
                expectedStatusId = fromStatus.Id,
                actualStatusId = detail.Ticket.StatusId,
            });

        // 6. Write the internal note FIRST. If the status flip fails
        //    afterwards the note is still in the timeline as visible
        //    intent — better than a status change without a recorded
        //    rationale (matters for ISO conformance).
        var motivationHtml = $"{noteHeadingHtml}<p>{HttpUtility.HtmlEncode(trimmed)}</p>";
        var note = await tickets.AddEventAsync(ticketId, new NewTicketEvent(
            EventType: "Note",
            BodyText: trimmed,
            BodyHtml: motivationHtml,
            IsInternal: true,
            AuthorUserId: userId), ct);
        if (note is null)
            return Results.NotFound();

        // 7. Flip the status. UpdateFieldsAsync triggers the regular
        //    StatusChange event, audit log, and downstream event/time
        //    trigger evaluators — admin-built automations chained on
        //    "ticket reaches closed" fire for ISO closures too.
        var updated = await tickets.UpdateFieldsAsync(
            ticketId,
            new TicketFieldUpdate(StatusId: toStatus.Id),
            userId,
            ct);
        if (updated is null)
            return Results.NotFound();

        // 8. ISO-scoped audit event. The structured payload makes the
        //    audit log directly auditor-readable without joining to
        //    ticket_events.
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: auditEventType,
            Actor: actor,
            ActorRole: role,
            Target: ticketId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new
            {
                ticketNumber = detail.Ticket.Number,
                fromStatus = fromStatus.Name,
                toStatus = toStatus.Name,
                motivation = trimmed,
                noteEventId = note.Id,
            }));

        // 9. SignalR fan-out. The detail page refetches on this event
        //    so the agent who clicked sees the new status + their note
        //    appear without a manual reload, and any colleague watching
        //    the same ticket sees the flip live.
        var idStr = ticketId.ToString();
        await hub.Clients.Group($"ticket:{idStr}").SendAsync("TicketUpdated", idStr, ct);
        await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", idStr, ct);

        return Results.Ok(new
        {
            ticketId,
            fromStatusId = fromStatus.Id,
            toStatusId = toStatus.Id,
            noteEventId = note.Id,
        });
    }

    // ---- Shared constants ------------------------------------------------

    public static class IsoSlugs
    {
        public const string MgmReview = "iso-mgm-review";
        public const string AwaitingIsms = "iso-awaiting-isms";
        public const string NoIncident = "iso-no-incident";
    }

    public static class IsoAuditEventTypes
    {
        public const string ClassifiedAsEvent = "iso.ticket.classified_as_event";
        public const string ClassifiedAsIncident = "iso.ticket.classified_as_incident";
    }
}
