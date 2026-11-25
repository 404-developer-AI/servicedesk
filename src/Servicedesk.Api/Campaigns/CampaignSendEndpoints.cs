using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Campaigns;

namespace Servicedesk.Api.Campaigns;

/// v0.1.67 — previewing and sending a campaign. A round only queues outbox
/// rows; <see cref="CampaignSendWorker"/> sends them one by one through the
/// normal single-ticket mail path, rechecking every rule right before the
/// mail leaves. Sending shares the "bulk" rate-limit policy.
internal static class CampaignSendEndpoints
{
    public static IEndpointRouteBuilder MapCampaignSendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/campaigns/{id:guid}")
            .WithTags("Campaigns")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.Campaigns);

        group.MapGet("/preview/{ticketId:guid}", async (Guid id, Guid ticketId, HttpContext http,
            ICampaignService svc, CancellationToken ct) =>
        {
            try
            {
                var item = await svc.PreviewAsync(id, TicketEndpoints.ResolveMutationActor(http), ticketId, ct);
                return item is null ? Results.NotFound() : Results.Ok(CampaignApi.MapPlanItem(item, includeBody: true));
            }
            catch (CampaignRejectedException ex) { return CampaignApi.Rejected(ex); }
        }).WithName("PreviewCampaignTicket");

        // Dry run of a round: who would get a mail and why the others not.
        group.MapPost("/plan", async (Guid id, [FromBody] CampaignPlanRequest req, HttpContext http,
            ICampaignService svc, CancellationToken ct) =>
        {
            if (CampaignApi.ValidateIds(req.TicketIds, required: false) is { } error) return Results.BadRequest(new { error });
            try
            {
                var plan = await svc.PlanAsync(id, TicketEndpoints.ResolveMutationActor(http),
                    req.Kind ?? CampaignRoundKinds.Initial, req.TicketIds, ct);
                return Results.Ok(new { items = plan.Select(p => CampaignApi.MapPlanItem(p, includeBody: false)) });
            }
            catch (CampaignRejectedException ex) { return CampaignApi.Rejected(ex); }
        }).WithName("PlanCampaignRound");

        group.MapPost("/rounds", async (Guid id, [FromBody] StartCampaignRoundRequest req, HttpContext http,
            ICampaignService svc, CancellationToken ct) =>
        {
            if (CampaignApi.ValidateIds(req.TicketIds, required: false) is { } error) return Results.BadRequest(new { error });
            try
            {
                var started = await svc.StartRoundAsync(id, TicketEndpoints.ResolveMutationActor(http),
                    new CampaignRoundRequest(req.Kind ?? CampaignRoundKinds.Initial, req.PendingStatusId,
                        req.PendingTillUtc, req.TicketIds, req.ScheduledUtc), ct);
                return Results.Ok(new
                {
                    roundId = started.RoundId,
                    roundNo = started.RoundNo,
                    queued = started.Queued,
                    skipped = started.Skipped,
                });
            }
            catch (CampaignRejectedException ex) { return CampaignApi.Rejected(ex); }
        }).WithName("StartCampaignRound").RequireRateLimiting("bulk");

        group.MapPost("/cancel-queued", async (Guid id, HttpContext http, ICampaignRepository campaigns,
            ICampaignOutboxRepository outbox, IQueueAccessService queueAccess, ICampaignNotifier notifier,
            Servicedesk.Infrastructure.Audit.IAuditLogger audit, CancellationToken ct) =>
        {
            if (await campaigns.GetAsync(id, ct) is null) return Results.NotFound();
            var scope = await queueAccess.GetScopeAsync(ActorContext.GetUserId(http), ActorContext.GetRole(http.User), ct);
            var cancelled = await outbox.CancelQueuedAsync(id, scope, ct);
            await CampaignEndpoints.AuditAsync(audit, http, "campaign.round_cancelled", id, new { cancelled }, ct);
            await notifier.CampaignChangedAsync(id, ct);
            return Results.Ok(new { cancelled });
        }).WithName("CancelCampaignQueued");

        group.MapPost("/retry-failed", async (Guid id, HttpContext http, ICampaignRepository campaigns,
            ICampaignOutboxRepository outbox, IQueueAccessService queueAccess, CampaignSendSignal signal,
            ICampaignNotifier notifier, Servicedesk.Infrastructure.Audit.IAuditLogger audit, CancellationToken ct) =>
        {
            if (await campaigns.GetAsync(id, ct) is null) return Results.NotFound();
            var scope = await queueAccess.GetScopeAsync(ActorContext.GetUserId(http), ActorContext.GetRole(http.User), ct);
            var retried = await outbox.RetryFailedAsync(id, scope, ct);
            await CampaignEndpoints.AuditAsync(audit, http, "campaign.retry_failed", id, new { retried }, ct);
            signal.Wake();
            await notifier.CampaignChangedAsync(id, ct);
            return Results.Ok(new { retried });
        }).WithName("RetryCampaignFailed").RequireRateLimiting("bulk");

        return app;
    }
}
