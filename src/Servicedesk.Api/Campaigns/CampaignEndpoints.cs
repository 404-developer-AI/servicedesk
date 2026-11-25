using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.ComposeTemplates;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Api.Campaigns;

/// v0.1.67 — Campaigns: one mail per ticket to many tickets at once
/// (ARCHITECTURE.md → Campaigns). Staff only and every route behind the
/// per-user Campaigns flag (403 when off, admins included). Campaign
/// metadata is shared among flag holders; the recipient rows are filtered
/// to the caller's queues with a visible hidden-count. Mail never leaves
/// from here: a round queues outbox rows that the worker sends one by one
/// through the normal single-ticket mail path.
public static class CampaignEndpoints
{
    public static IEndpointRouteBuilder MapCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/campaigns")
            .WithTags("Campaigns")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.Campaigns);

        group.MapGet("/", async ([FromQuery] bool? includeArchived, [FromQuery] Guid? projectTicketId,
            ICampaignRepository campaigns, CancellationToken ct) =>
        {
            var items = await campaigns.ListAsync(includeArchived ?? false, projectTicketId, ct);
            return Results.Ok(new { items });
        }).WithName("ListCampaigns");

        group.MapGet("/{id:guid}", GetDetail).WithName("GetCampaign");
        group.MapPost("/", Create).WithName("CreateCampaign");
        group.MapPatch("/{id:guid}", Update).WithName("UpdateCampaign");
        group.MapPost("/{id:guid}/duplicate", Duplicate).WithName("DuplicateCampaign");

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ICampaignRepository campaigns,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (await campaigns.GetAsync(id, ct) is null) return Results.NotFound();
            if (!await campaigns.DeleteUnsentAsync(id, ct))
                return Results.Conflict(new
                {
                    error = "This campaign already sent mail. Archive it instead — its history stays on the tickets.",
                    code = "campaign_has_rounds",
                });
            await AuditAsync(audit, http, "campaign.deleted", id, null, ct);
            return Results.NoContent();
        }).WithName("DeleteCampaign");

        // Token list for the composer: the compose tokens plus the campaign ones.
        group.MapGet("/tokens", () => Results.Ok(new
        {
            tokens = ComposeTokens.Supported.Select(t => new { token = t.Token, label = t.Label })
                .Concat(CampaignTokens.Supported.Select(t => new { token = t.Token, label = t.Label })),
        })).WithName("ListCampaignTokens");

        // Agent-readable projection of the Campaigns settings (never the
        // admin-only /api/settings).
        app.MapGet("/api/settings/campaigns", async (ISettingsService settings, CancellationToken ct) =>
            Results.Ok(new
            {
                enabled = await settings.GetOrAsync<bool>(SettingKeys.Campaigns.Enabled, true, ct),
                maxRecipients = await settings.GetOrAsync<int>(SettingKeys.Campaigns.MaxRecipients, 500, ct),
                sendRatePerMinute = await settings.GetOrAsync<int>(SettingKeys.Campaigns.SendRatePerMinute, 20, ct),
            }))
            .WithTags("Campaigns")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.Campaigns)
            .WithName("GetCampaignSettings");

        app.MapCampaignRecipientEndpoints();
        app.MapCampaignSendEndpoints();
        app.MapCampaignAnswerEndpoints();
        Servicedesk.Api.Maintenance.MaintenanceEndpoints.MapMaintenanceEndpoints(app);
        Servicedesk.Api.Maintenance.MaintenanceNoteEndpoints.MapMaintenanceNoteEndpoints(app);
        return app;
    }

    private static async Task<IResult> GetDetail(Guid id, HttpContext http, ICampaignRepository campaigns,
        ICampaignDashboardReader dashboard, IQueueAccessService queueAccess, ITicketRepository tickets,
        TimeProvider clock, CancellationToken ct)
    {
        var campaign = await campaigns.GetAsync(id, ct);
        if (campaign is null) return Results.NotFound();

        var userId = ActorContext.GetUserId(http);
        var role = ActorContext.GetRole(http.User);
        var scope = await queueAccess.GetScopeAsync(userId, role, ct);
        var board = await dashboard.ReadAsync(id, scope, clock.GetUtcNow().UtcDateTime, ct);
        var rounds = await campaigns.ListRoundsAsync(id, ct);

        // The linked project's subject only for callers who may open it.
        var projectVisible = campaign.ProjectTicketId is { } pid
            && await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, pid, ct) is not null;

        return Results.Ok(new
        {
            id = campaign.Id,
            name = campaign.Name,
            subject = campaign.Subject,
            bodyHtml = campaign.BodyHtml,
            otherTimeNotice = campaign.OtherTimeNotice,
            sourceTemplateId = campaign.SourceTemplateId,
            state = campaign.State,
            defaultProposedUtc = campaign.DefaultProposedUtc,
            project = campaign.ProjectTicketId is null ? null : new
            {
                id = campaign.ProjectTicketId,
                number = projectVisible ? campaign.ProjectTicketNumber : null,
                subject = projectVisible ? campaign.ProjectTicketSubject : null,
                visible = projectVisible,
            },
            duplicatedFromId = campaign.DuplicatedFromId,
            maintenance = campaign.MaintenancePlanId is null ? null : new
            {
                planId = campaign.MaintenancePlanId,
                planName = campaign.MaintenancePlanName,
                roundId = campaign.MaintenanceRoundId,
            },
            createdByEmail = campaign.CreatedByEmail,
            createdUtc = campaign.CreatedUtc,
            updatedUtc = campaign.UpdatedUtc,
            rounds = rounds.Select(CampaignApi.MapRound),
            recipients = board.Recipients.Select(CampaignApi.MapRecipient),
            hiddenCount = board.HiddenCount,
        });
    }

    private static async Task<IResult> Create([FromBody] CreateCampaignRequest req, HttpContext http,
        ICampaignRepository campaigns, ICampaignRecipientRepository recipients, IQueueAccessService queueAccess,
        ITicketRepository tickets, ITicketHtmlSanitizer sanitizer, ISettingsService settings, IAuditLogger audit,
        CancellationToken ct)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > CampaignApi.MaxNameLength)
            return Results.BadRequest(new { error = $"Name is required (at most {CampaignApi.MaxNameLength} characters)." });
        if (ValidateMessage(req.Subject, req.BodyHtml, req.OtherTimeNotice) is { } msgError)
            return Results.BadRequest(new { error = msgError });
        if (CampaignApi.ValidateIds(req.TicketIds, required: false) is { } idError)
            return Results.BadRequest(new { error = idError });

        var userId = ActorContext.GetUserId(http);
        var role = ActorContext.GetRole(http.User);
        if (req.ProjectTicketId is { } projectId && !await IsAccessibleProjectAsync(tickets, queueAccess, userId, role, projectId, ct))
            return Results.BadRequest(new { error = "Pick a project ticket you have access to.", code = "invalid_project" });

        var id = await campaigns.CreateAsync(new NewCampaign(
            name, (req.Subject ?? string.Empty).Trim(), sanitizer.Sanitize(req.BodyHtml ?? string.Empty),
            req.SourceTemplateId, req.ProjectTicketId, req.DefaultProposedUtc, userId,
            OtherTimeNotice: (req.OtherTimeNotice ?? string.Empty).Trim()), ct);

        var ticketIds = new List<Guid>(req.TicketIds ?? Array.Empty<Guid>());
        if (req.AddProjectTickets == true && req.ProjectTicketId is { } pid)
            ticketIds.AddRange(await recipients.OpenProjectTicketIdsAsync(pid, ct));
        CampaignAddResult? added = null;
        if (ticketIds.Count > 0)
        {
            var scope = await queueAccess.GetScopeAsync(userId, role, ct);
            var max = await settings.GetOrAsync<int>(SettingKeys.Campaigns.MaxRecipients, 500, ct);
            var source = req.AddProjectTickets == true ? CampaignTicketSources.Project : CampaignTicketSources.Selection;
            added = await recipients.AddAsync(id, ticketIds, source, userId, scope, Math.Clamp(max, 1, 2000), ct);
        }

        await AuditAsync(audit, http, "campaign.created", id, new { name, req.ProjectTicketId, added = added?.Added ?? 0 }, ct);
        return Results.Created($"/api/campaigns/{id}", new { id, added });
    }

    private static async Task<IResult> Update(Guid id, [FromBody] UpdateCampaignRequest req, HttpContext http,
        ICampaignRepository campaigns, IQueueAccessService queueAccess, ITicketRepository tickets,
        ITicketHtmlSanitizer sanitizer, ICampaignNotifier notifier, IAuditLogger audit, CancellationToken ct)
    {
        if (req.Name is { } n && (n.Trim().Length is 0 or > CampaignApi.MaxNameLength))
            return Results.BadRequest(new { error = $"Name must be 1 to {CampaignApi.MaxNameLength} characters." });
        if (ValidateMessage(req.Subject, req.BodyHtml, req.OtherTimeNotice) is { } msgError)
            return Results.BadRequest(new { error = msgError });
        if (req.State is not null && !CampaignStates.IsValid(req.State))
            return Results.BadRequest(new { error = "state must be Draft, Active or Archived." });

        var campaign = await campaigns.GetAsync(id, ct);
        if (campaign is null) return Results.NotFound();

        var userId = ActorContext.GetUserId(http);
        var role = ActorContext.GetRole(http.User);
        if (req.ProjectTicketId is { } projectId && !await IsAccessibleProjectAsync(tickets, queueAccess, userId, role, projectId, ct))
            return Results.BadRequest(new { error = "Pick a project ticket you have access to.", code = "invalid_project" });

        await campaigns.UpdateAsync(id, new CampaignUpdate(
            Name: req.Name?.Trim(),
            Subject: req.Subject?.Trim(),
            BodyHtml: req.BodyHtml is null ? null : sanitizer.Sanitize(req.BodyHtml),
            SourceTemplateId: req.SourceTemplateId,
            ProjectTicketId: req.ProjectTicketId,
            ClearProject: req.ClearProject == true,
            DefaultProposedUtc: req.DefaultProposedUtc,
            ClearDefaultProposed: req.ClearDefaultProposed == true,
            State: req.State,
            OtherTimeNotice: req.OtherTimeNotice?.Trim()), ct);

        await AuditAsync(audit, http, "campaign.updated", id, new
        {
            name = req.Name is not null,
            message = req.Subject is not null || req.BodyHtml is not null,
            req.ProjectTicketId,
            clearProject = req.ClearProject == true,
            req.DefaultProposedUtc,
            req.State,
        }, ct);
        await notifier.CampaignChangedAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Duplicate(Guid id, [FromBody] DuplicateCampaignRequest? req, HttpContext http,
        ICampaignRepository campaigns, ICampaignRecipientRepository recipients, ICampaignDashboardReader dashboard,
        IQueueAccessService queueAccess, ISettingsService settings, TimeProvider clock, IAuditLogger audit,
        CancellationToken ct)
    {
        var source = await campaigns.GetAsync(id, ct);
        if (source is null) return Results.NotFound();
        var name = string.IsNullOrWhiteSpace(req?.Name) ? $"{source.Name} (copy)" : req!.Name!.Trim();
        if (name.Length > CampaignApi.MaxNameLength) name = name[..CampaignApi.MaxNameLength];

        var userId = ActorContext.GetUserId(http);
        var role = ActorContext.GetRole(http.User);
        var newId = await campaigns.CreateAsync(new NewCampaign(
            name, source.Subject, source.BodyHtml, source.SourceTemplateId, source.ProjectTicketId,
            DefaultProposedUtc: null, userId, DuplicatedFromId: id, OtherTimeNotice: source.OtherTimeNotice), ct);

        // The recipients the caller can see, without dates or decisions —
        // a new round proposes new dates.
        var scope = await queueAccess.GetScopeAsync(userId, role, ct);
        var board = await dashboard.ReadAsync(id, scope, clock.GetUtcNow().UtcDateTime, ct);
        var max = await settings.GetOrAsync<int>(SettingKeys.Campaigns.MaxRecipients, 500, ct);
        var added = await recipients.AddAsync(newId, board.Recipients.Select(r => r.Row.TicketId).ToList(),
            CampaignTicketSources.Selection, userId, scope, Math.Clamp(max, 1, 2000), ct);

        await AuditAsync(audit, http, "campaign.duplicated", newId, new { from = id, added = added.Added }, ct);
        return Results.Created($"/api/campaigns/{newId}", new { id = newId, added });
    }

    /// Plain text shown to the customer on the answer page with "Another moment".
    public const int MaxOtherTimeNoticeLength = 1000;

    private static string? ValidateMessage(string? subject, string? bodyHtml, string? otherTimeNotice = null)
    {
        if (otherTimeNotice is { Length: > MaxOtherTimeNoticeLength })
            return $"The note for \"Another moment\" is at most {MaxOtherTimeNoticeLength} characters.";
        if (subject is { Length: > CampaignApi.MaxSubjectLength })
            return $"Subject is at most {CampaignApi.MaxSubjectLength} characters.";
        if (bodyHtml is { Length: > CampaignApi.MaxBodyLength })
            return "Message is too long.";
        return null;
    }

    private static async Task<bool> IsAccessibleProjectAsync(ITicketRepository tickets, IQueueAccessService queueAccess,
        Guid userId, string role, Guid projectId, CancellationToken ct)
    {
        var ticket = await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, projectId, ct);
        return ticket is { IsProject: true, IsDeleted: false };
    }

    internal static Task AuditAsync(IAuditLogger audit, HttpContext http, string eventType, Guid campaignId,
        object? payload, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: campaignId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload), ct);
    }
}
