using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Campaigns;

/// One ticket in a round plan: what would be mailed, or why not.
public sealed record CampaignPlanItem(
    Guid TicketId,
    long Number,
    string TicketSubject,
    string? RequesterName,
    string? RequesterEmail,
    string? CompanyName,
    DateTime? ProposedUtc,
    string? SkipReason,
    IReadOnlyList<string> MissingTokens,
    string RenderedSubject,
    string RenderedBodyHtml);

public sealed record CampaignRoundRequest(
    string Kind,
    Guid? PendingStatusId,
    DateTime? PendingTillUtc,
    IReadOnlyCollection<Guid>? TicketIds = null,
    /// Phase 2 — "send on …" (server time); null sends now.
    DateTime? ScheduledUtc = null,
    /// Phase 3 — the maintenance reminder's own message for this round only
    /// (already sanitised); null uses the campaign's message.
    string? SubjectOverride = null,
    string? BodyHtmlOverride = null);

public sealed record CampaignRoundStarted(Guid RoundId, int RoundNo, int Queued, int Skipped);

/// Thrown for a request the campaign cannot take (400/409 at the endpoint).
public sealed class CampaignRejectedException : Exception
{
    public string Code { get; }
    public CampaignRejectedException(string code, string message) : base(message) => Code = code;
}

public interface ICampaignService
{
    /// Renders the campaign for one recipient, whatever its send state.
    Task<CampaignPlanItem?> PreviewAsync(Guid campaignId, TicketMutationActor actor, Guid ticketId, CancellationToken ct);

    /// Dry run: every candidate of a round with its rendered mail or skip reason.
    Task<IReadOnlyList<CampaignPlanItem>> PlanAsync(Guid campaignId, TicketMutationActor actor, string kind,
        IReadOnlyCollection<Guid>? ticketIds, CancellationToken ct);

    Task<CampaignRoundStarted> StartRoundAsync(Guid campaignId, TicketMutationActor actor, CampaignRoundRequest request, CancellationToken ct);
}

/// v0.1.67 — turns a campaign into outbox rows. Candidates of an initial
/// round are recipients never mailed; a follow-up goes only to recipients
/// that were mailed, did not answer and have no decision yet. Each mail is
/// rendered here (frozen) and every ticket runs <see cref="CampaignEligibility"/>;
/// the send step checks again right before the mail leaves.
public sealed class CampaignService : ICampaignService
{
    /// Internal plan mode: render a recipient regardless of its send state.
    private const string PreviewKind = "Preview";

    private readonly ICampaignRepository _campaigns;
    private readonly ICampaignDashboardReader _dashboard;
    private readonly ICampaignOutboxRepository _outbox;
    private readonly IComposeTokenResolver _tokens;
    private readonly IQueueAccessService _queueAccess;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;
    private readonly CampaignSendSignal _signal;
    private readonly ICampaignNotifier _notifier;
    private readonly ICampaignApprovalLinks _links;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;

    public CampaignService(
        ICampaignRepository campaigns, ICampaignDashboardReader dashboard, ICampaignOutboxRepository outbox,
        IComposeTokenResolver tokens, IQueueAccessService queueAccess, ISettingsService settings,
        IAuditLogger audit, CampaignSendSignal signal, ICampaignNotifier notifier, ICampaignApprovalLinks links,
        NpgsqlDataSource dataSource, TimeProvider clock)
    {
        _links = links;
        _campaigns = campaigns;
        _dashboard = dashboard;
        _outbox = outbox;
        _tokens = tokens;
        _queueAccess = queueAccess;
        _settings = settings;
        _audit = audit;
        _signal = signal;
        _notifier = notifier;
        _dataSource = dataSource;
        _clock = clock;
    }

    public async Task<IReadOnlyList<CampaignPlanItem>> PlanAsync(Guid campaignId, TicketMutationActor actor, string kind,
        IReadOnlyCollection<Guid>? ticketIds, CancellationToken ct)
    {
        RequireRoundKind(kind);
        var campaign = await _campaigns.GetAsync(campaignId, ct)
            ?? throw new CampaignRejectedException("not_found", "Campaign not found.");
        return await BuildPlanAsync(campaign, actor, kind, ticketIds, scheduledUtc: null, mintLinks: false, ct);
    }

    public async Task<CampaignPlanItem?> PreviewAsync(Guid campaignId, TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var campaign = await _campaigns.GetAsync(campaignId, ct)
            ?? throw new CampaignRejectedException("not_found", "Campaign not found.");
        var plan = await BuildPlanAsync(campaign, actor, PreviewKind, new[] { ticketId }, scheduledUtc: null, mintLinks: false, ct);
        return plan.FirstOrDefault();
    }

    public async Task<CampaignRoundStarted> StartRoundAsync(Guid campaignId, TicketMutationActor actor,
        CampaignRoundRequest request, CancellationToken ct)
    {
        RequireRoundKind(request.Kind);
        if (!await _settings.GetOrAsync<bool>(SettingKeys.Campaigns.Enabled, true, ct))
            throw new CampaignRejectedException("campaigns_disabled", "Campaigns are switched off in Settings.");

        var campaign = await _campaigns.GetAsync(campaignId, ct)
            ?? throw new CampaignRejectedException("not_found", "Campaign not found.");
        if (campaign.State == CampaignStates.Archived)
            throw new CampaignRejectedException("archived", "This campaign is archived. Restore it before sending.");
        if (!string.IsNullOrWhiteSpace(request.SubjectOverride) && !string.IsNullOrWhiteSpace(request.BodyHtmlOverride))
        {
            // This round only: frozen on the round and rendered per ticket like the campaign's own message.
            campaign.Subject = request.SubjectOverride;
            campaign.BodyHtml = request.BodyHtmlOverride;
        }
        if (string.IsNullOrWhiteSpace(campaign.Subject) || string.IsNullOrWhiteSpace(campaign.BodyHtml))
            throw new CampaignRejectedException("message_incomplete", "Write a subject and a message before sending.");

        await ValidateScheduleAsync(request, ct);
        await ValidatePendingAsync(request, ct);

        var plan = await BuildPlanAsync(campaign, actor, request.Kind, request.TicketIds, request.ScheduledUtc, mintLinks: true, ct);
        if (plan.Count == 0)
            throw new CampaignRejectedException("nothing_to_send", request.Kind == CampaignRoundKinds.FollowUp
                ? "Every mailed ticket already has an answer or a decision."
                : "There are no tickets left that were not mailed yet.");
        if (plan.All(p => p.SkipReason is not null))
            throw new CampaignRejectedException("all_skipped", AllSkippedMessage(plan));

        var messages = plan.Select(p => new NewCampaignMessage(
            p.TicketId,
            p.SkipReason is null ? CampaignMessageStates.Queued : CampaignMessageStates.Skipped,
            p.SkipReason,
            (p.RequesterEmail ?? string.Empty).Trim(),
            p.SkipReason is null ? p.RenderedSubject : string.Empty,
            p.SkipReason is null ? p.RenderedBodyHtml : string.Empty)).ToList();

        var (roundId, roundNo) = await _outbox.EnqueueRoundAsync(new NewCampaignRound(
            campaignId, request.Kind, campaign.Subject, campaign.BodyHtml, actor.UserId, actor.Role,
            request.PendingStatusId, request.PendingStatusId is null ? null : request.PendingTillUtc,
            request.ScheduledUtc), messages, ct);

        var queued = messages.Count(m => m.State == CampaignMessageStates.Queued);
        await _audit.LogAsync(new AuditEvent(
            EventType: "campaign.sent",
            Actor: actor.AuditActor,
            ActorRole: actor.AuditRole,
            Target: campaignId.ToString(),
            ClientIp: actor.ClientIp,
            UserAgent: actor.UserAgent,
            Payload: new
            {
                roundId,
                roundNo,
                kind = request.Kind,
                queued,
                skipped = messages.Count - queued,
                skippedReasons = plan.Where(p => p.SkipReason is not null)
                    .GroupBy(p => p.SkipReason!).ToDictionary(g => g.Key, g => g.Count()),
                request.PendingStatusId,
                request.PendingTillUtc,
                request.ScheduledUtc,
            }), ct);

        _signal.Wake();
        await _notifier.CampaignChangedAsync(campaignId, ct);
        return new CampaignRoundStarted(roundId, roundNo, queued, messages.Count - queued);
    }

    /// <paramref name="mintLinks"/>: create the recipients' answer links for
    /// real (a round being queued); otherwise a preview URL is rendered.
    private async Task<IReadOnlyList<CampaignPlanItem>> BuildPlanAsync(CampaignRow campaign, TicketMutationActor actor,
        string kind, IReadOnlyCollection<Guid>? ticketIds, DateTime? scheduledUtc, bool mintLinks, CancellationToken ct)
    {
        var scope = await _queueAccess.GetScopeAsync(actor.UserId, actor.Role, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var board = await _dashboard.ReadAsync(campaign.Id, scope, now, ct);
        var inFlight = await _outbox.TicketsInFlightAsync(campaign.Id, ct);
        var subset = ticketIds?.ToHashSet();

        var candidates = board.Recipients
            .Where(r => kind == PreviewKind || !inFlight.Contains(r.Row.TicketId))
            .Where(r => subset is null || subset.Contains(r.Row.TicketId))
            .Where(r => kind switch
            {
                CampaignRoundKinds.Initial => r.Row.FirstSentUtc is null,
                CampaignRoundKinds.FollowUp => r.Row.FirstSentUtc is not null && r.Row.ReplyUtc is null && r.Row.Decision is null,
                _ => true,
            })
            .Select(r => r.Row)
            .ToList();
        if (candidates.Count == 0) return Array.Empty<CampaignPlanItem>();

        var tokenMaps = await _tokens.ResolveForTicketsAsync(
            candidates.Select(c => c.TicketId).ToList(), actor.AuditActor, ct);
        var format = await CampaignFormat.LoadAsync(_settings, ct);
        var projectsOn = await ProjectMailGuard.IsBlockedAsync(_settings, isProject: true, ct);

        // Phase 2 — the answer link, only when the message uses it. Its
        // validity is measured from the moment the mail leaves.
        IReadOnlyDictionary<Guid, CampaignLinkPlan>? links = null;
        var linkLabel = string.Empty;
        if (CampaignRenderer.UsesApprovalLink(campaign.Subject, campaign.BodyHtml))
        {
            links = await _links.PlanAsync(campaign.Id,
                candidates.ToDictionary(c => c.TicketId, c => c.ProposedUtc ?? campaign.DefaultProposedUtc),
                scheduledUtc ?? now, mintLinks, ct);
            linkLabel = (await _settings.GetOrAsync<string?>(SettingKeys.Campaigns.ApprovalLinkLabel, null, ct))?.Trim() is { Length: > 0 } l
                ? l : "Answer online";
        }

        var plan = new List<CampaignPlanItem>(candidates.Count);
        foreach (var r in candidates)
        {
            var map = tokenMaps.TryGetValue(r.TicketId, out var m) ? m : new Dictionary<string, string>();
            var proposed = r.ProposedUtc ?? campaign.DefaultProposedUtc;
            var link = links is not null && links.TryGetValue(r.TicketId, out var lp) ? lp : null;
            var rendered = CampaignRenderer.Render(campaign.Subject, campaign.BodyHtml, campaign.Name, proposed, map, format,
                link?.Url, linkLabel);
            var reason = CampaignEligibility.Check(
                projectsOn && r.IsProject, r.IsMerged, r.StateCategory, r.RequesterEmail,
                link?.SkipReason is null ? rendered.MissingTokens : null)
                ?? link?.SkipReason;
            plan.Add(new CampaignPlanItem(
                r.TicketId, r.Number, r.Subject, r.RequesterName, r.RequesterEmail, r.CompanyName, proposed,
                reason, rendered.MissingTokens, rendered.Subject, rendered.BodyHtml));
        }
        return plan;
    }

    /// "None of the tickets can be mailed: 3 × missing_tokens ({{x}})." — the
    /// reasons travel with the error, so an automatic round (maintenance
    /// planner) shows why without opening the campaign.
    private static string AllSkippedMessage(IReadOnlyList<CampaignPlanItem> plan)
    {
        var parts = plan.GroupBy(p => p.SkipReason!).Select(g => g.Key == CampaignSkipReasons.MissingTokens
            ? $"{g.Count()} × placeholder without a value ({string.Join(", ", g.SelectMany(p => p.MissingTokens).Distinct())})"
            : $"{g.Count()} × {g.Key}");
        return $"None of the tickets can be mailed: {string.Join("; ", parts)}. Check the issues in the list.";
    }

    private static void RequireRoundKind(string kind)
    {
        if (kind is not (CampaignRoundKinds.Initial or CampaignRoundKinds.FollowUp))
            throw new CampaignRejectedException("invalid_kind", "kind must be Initial or FollowUp.");
    }

    private async Task ValidateScheduleAsync(CampaignRoundRequest request, CancellationToken ct)
    {
        if (request.ScheduledUtc is not { } at) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        var maxDays = Math.Clamp(await _settings.GetOrAsync<int>(SettingKeys.Campaigns.MaxScheduleDays, 90, ct), 1, 365);
        if (at <= now.AddMinutes(1))
            throw new CampaignRejectedException("schedule_in_past", "Pick a send moment in the future.");
        if (at > now.AddDays(maxDays))
            throw new CampaignRejectedException("schedule_too_far", $"A round can be scheduled at most {maxDays} days ahead.");
        if (request.PendingTillUtc is { } till && till <= at)
            throw new CampaignRejectedException("pending_before_send", "The pending-till date must be after the send moment.");
    }

    private async Task ValidatePendingAsync(CampaignRoundRequest request, CancellationToken ct)
    {
        if (request.PendingStatusId is null)
        {
            if (request.PendingTillUtc is not null)
                throw new CampaignRejectedException("pending_status_required", "Pick the pending status to set.");
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var category = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state_category FROM statuses WHERE id = @id AND is_active = TRUE",
            new { id = request.PendingStatusId }, cancellationToken: ct));
        if (category is null)
            throw new CampaignRejectedException("unknown_status", "That status does not exist.");
        if (request.PendingTillUtc is not null && category != "Pending")
            throw new CampaignRejectedException(PendingTillRules.NotPendingCode, "A pending-till date needs a Pending status.");
        if (PendingTillRules.IsInPast(request.PendingTillUtc, _clock.GetUtcNow().UtcDateTime))
            throw new CampaignRejectedException(PendingTillRules.PastCode, PendingTillRules.PastMessage);
    }
}
