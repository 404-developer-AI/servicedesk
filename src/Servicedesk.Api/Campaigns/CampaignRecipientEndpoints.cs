using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Api.Campaigns;

/// v0.1.67 — the recipient list of a campaign: add tickets (a selection or a
/// project's open linked tickets), remove never-mailed ones, set proposed
/// dates, and record the agent's decision on a customer's answer. Every
/// write is limited to tickets in the caller's queues (in SQL).
internal static class CampaignRecipientEndpoints
{
    public static IEndpointRouteBuilder MapCampaignRecipientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/campaigns/{id:guid}/tickets")
            .WithTags("Campaigns")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.Campaigns);

        group.MapPost("/", async (Guid id, [FromBody] CampaignTicketIdsRequest req, HttpContext http,
            ICampaignRepository campaigns, ICampaignRecipientRepository recipients, IQueueAccessService queueAccess,
            ISettingsService settings, ICampaignNotifier notifier, IAuditLogger audit, CancellationToken ct) =>
        {
            if (CampaignApi.ValidateIds(req.TicketIds) is { } error) return Results.BadRequest(new { error });
            return await AddAsync(id, req.TicketIds!, CampaignTicketSources.Selection, http, campaigns, recipients,
                queueAccess, settings, notifier, audit, ct);
        }).WithName("AddCampaignTickets");

        group.MapPost("/from-project", async (Guid id, HttpContext http,
            ICampaignRepository campaigns, ICampaignRecipientRepository recipients, IQueueAccessService queueAccess,
            ITicketRepository tickets, ISettingsService settings, ICampaignNotifier notifier, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var campaign = await campaigns.GetAsync(id, ct);
            if (campaign is null) return Results.NotFound();
            if (campaign.ProjectTicketId is not { } projectId)
                return Results.BadRequest(new { error = "Link a project ticket to this campaign first.", code = "no_project" });
            var userId = ActorContext.GetUserId(http);
            var role = ActorContext.GetRole(http.User);
            if (await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, projectId, ct) is null)
                return Results.NotFound();

            var ids = await recipients.OpenProjectTicketIdsAsync(projectId, ct);
            return await AddAsync(id, ids, CampaignTicketSources.Project, http, campaigns, recipients,
                queueAccess, settings, notifier, audit, ct);
        }).WithName("AddCampaignProjectTickets");

        group.MapPost("/remove", async (Guid id, [FromBody] CampaignTicketIdsRequest req, HttpContext http,
            ICampaignRepository campaigns, ICampaignRecipientRepository recipients, IQueueAccessService queueAccess,
            ICampaignNotifier notifier, IAuditLogger audit, CancellationToken ct) =>
        {
            if (CampaignApi.ValidateIds(req.TicketIds) is { } error) return Results.BadRequest(new { error });
            if (await campaigns.GetAsync(id, ct) is null) return Results.NotFound();
            var scope = await ScopeAsync(http, queueAccess, ct);
            var removed = await recipients.RemoveUnsentAsync(id, req.TicketIds!, scope, ct);
            await CampaignEndpoints.AuditAsync(audit, http, "campaign.recipients_removed", id,
                new { requested = req.TicketIds!.Count, removed }, ct);
            await notifier.CampaignChangedAsync(id, ct);
            // Tickets already mailed stay: their mail is on the timeline.
            return Results.Ok(new { removed, kept = req.TicketIds!.Count - removed });
        }).WithName("RemoveCampaignTickets");

        group.MapPut("/proposed", async (Guid id, [FromBody] CampaignProposedRequest req, HttpContext http,
            ICampaignRepository campaigns, ICampaignRecipientRepository recipients, IQueueAccessService queueAccess,
            ICampaignNotifier notifier, CancellationToken ct) =>
        {
            if (CampaignApi.ValidateIds(req.TicketIds) is { } error) return Results.BadRequest(new { error });
            if (await campaigns.GetAsync(id, ct) is null) return Results.NotFound();
            var scope = await ScopeAsync(http, queueAccess, ct);
            var updated = await recipients.SetProposedAsync(id, req.TicketIds!, req.ProposedUtc, scope, ct);
            await notifier.CampaignChangedAsync(id, ct);
            return Results.Ok(new { updated });
        }).WithName("SetCampaignProposedDates");

        group.MapPut("/{ticketId:guid}/decision", async (Guid id, Guid ticketId, [FromBody] CampaignDecisionRequest req,
            HttpContext http, ICampaignRecipientRepository recipients, IQueueAccessService queueAccess,
            ICampaignNotifier notifier, IAuditLogger audit, CancellationToken ct) =>
        {
            var decision = string.IsNullOrWhiteSpace(req.Decision) ? null : req.Decision;
            if (decision is not null && !CampaignDecisions.IsValid(decision))
                return Results.BadRequest(new { error = "decision must be Approved, OtherTime, Declined or empty." });
            var scope = await ScopeAsync(http, queueAccess, ct);
            var userId = ActorContext.GetUserId(http);
            if (!await recipients.SetDecisionAsync(id, ticketId, decision, userId, scope, ct))
                return Results.NotFound();
            await CampaignEndpoints.AuditAsync(audit, http, "campaign.decision", id, new { ticketId, decision }, ct);
            await notifier.CampaignChangedAsync(id, ct);
            return Results.NoContent();
        }).WithName("SetCampaignDecision");

        return app;
    }

    private static async Task<IResult> AddAsync(Guid id, IReadOnlyCollection<Guid> ticketIds, string source,
        HttpContext http, ICampaignRepository campaigns, ICampaignRecipientRepository recipients,
        IQueueAccessService queueAccess, ISettingsService settings, ICampaignNotifier notifier, IAuditLogger audit,
        CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(id, ct);
        if (campaign is null) return Results.NotFound();
        if (campaign.State == CampaignStates.Archived)
            return Results.Conflict(new { error = "This campaign is archived.", code = "archived" });

        var scope = await ScopeAsync(http, queueAccess, ct);
        var max = Math.Clamp(await settings.GetOrAsync<int>(SettingKeys.Campaigns.MaxRecipients, 500, ct), 1, 2000);
        var userId = ActorContext.GetUserId(http);
        var result = await recipients.AddAsync(id, ticketIds, source, userId, scope, max, ct);

        await CampaignEndpoints.AuditAsync(audit, http, "campaign.recipients_added", id,
            new { source, requested = ticketIds.Count, result.Added, result.AlreadyInCampaign, result.NotAllowed }, ct);
        await notifier.CampaignChangedAsync(id, ct);
        var total = await recipients.CountAsync(id, ct);
        return Results.Ok(new
        {
            added = result.Added,
            alreadyInCampaign = result.AlreadyInCampaign,
            notAllowed = result.NotAllowed,
            total,
            atCapacity = total >= max,
        });
    }

    private static async Task<QueueAccessScope> ScopeAsync(HttpContext http, IQueueAccessService queueAccess, CancellationToken ct)
        => await queueAccess.GetScopeAsync(ActorContext.GetUserId(http), ActorContext.GetRole(http.User), ct);
}
