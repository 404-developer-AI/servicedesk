using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.TicketTemplates;

namespace Servicedesk.Infrastructure.Maintenance;

/// v0.1.67 (phase 3) — tokens for the round tickets on top of the compose
/// tokens (contact, company).
public static partial class MaintenanceTokens
{
    public const string PlanName = "{{plan.name}}";
    public const string InterventionDate = "{{plan.interventionDate}}";
    public const string MemberLabel = "{{member.label}}";

    public static readonly IReadOnlyList<(string Token, string Label)> Supported = new[]
    {
        (PlanName, "Maintenance · Plan name"),
        (InterventionDate, "Maintenance · Intervention date"),
        (MemberLabel, "Maintenance · Member label"),
    };

    /// The plan-level tokens, also usable in the plan's mail: they are the
    /// same for every ticket of a round, so they are filled in when the
    /// round's campaign is made. {{member.label}} differs per ticket and is
    /// ticket-only.
    public static readonly IReadOnlyList<(string Token, string Label)> Mail = new[]
    {
        (PlanName, "Maintenance · Plan name"),
        (InterventionDate, "Maintenance · Intervention date"),
    };

    /// The {{tokens}} in <paramref name="text"/> that are not in
    /// <paramref name="allowed"/> — a plan with one would only fail later,
    /// at send time, as "missing placeholder".
    public static IReadOnlyList<string> Unknown(string? text, IEnumerable<string> allowed) =>
        ComposeTokenRenderer.Render(text, allowed.Distinct().ToDictionary(t => t, _ => "x"), ComposeTokenEscape.PlainText).MissingTokens;

    /// The label is optional: for a member without one, {{member.label}} and
    /// the separator in front of it ("Plan — {{member.label}}") disappear
    /// instead of staying in the ticket as a literal placeholder.
    public static string? DropEmptyLabel(string? template, string? label) =>
        template is null || !string.IsNullOrWhiteSpace(label) ? template
            : EmptyLabelRegex().Replace(template, string.Empty);

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[—–\-:|·,]?\s*\{\{\s*member\.label\s*\}\}")]
    private static partial System.Text.RegularExpressions.Regex EmptyLabelRegex();

    /// Fills in the plan-level tokens of a round's mail (subject plain text,
    /// body HTML-encoded); every other token stays for the campaign renderer.
    public static (string Subject, string BodyHtml) RenderPlanLevel(string subject, string bodyHtml,
        string planName, DateTime interventionDate, CampaignFormat format)
    {
        var map = new Dictionary<string, string>
        {
            [PlanName] = planName,
            [InterventionDate] = interventionDate.ToString(format.DateFormat, format.Culture),
        };
        return (ComposeTokenRenderer.Render(subject, map, ComposeTokenEscape.PlainText).Output,
                ComposeTokenRenderer.Render(bodyHtml, map, ComposeTokenEscape.Html).Output);
    }
}

/// The plan's automatic sender as a campaign actor — rechecked every time
/// (active Agent/Admin with the Campaigns flag), else the send is refused.
public static class MaintenanceSender
{
    public static async Task<TicketMutationActor> ResolveAsync(IUserService users, Guid? userId, CancellationToken ct)
    {
        var sender = userId is { } uid ? await users.FindByIdAsync(uid, ct) : null;
        if (sender is not { IsActive: true } || sender.RoleName is not ("Agent" or "Admin")
            || !await users.GetCampaignsEnabledAsync(sender.Id, ct))
        {
            throw new InvalidOperationException(
                "Auto-send is on, but the plan's sender can no longer send campaigns. Open the plan and save it again as a user with Campaigns access, or send the campaign by hand.");
        }
        return new TicketMutationActor(sender.Id, sender.RoleName, sender.Email, sender.RoleName, null, MaintenanceRoundService.Origin);
    }
}

public interface IMaintenanceRoundService
{
    /// Starts every round whose mail day has come and finishes open rounds.
    /// Returns how many rounds were handled.
    Task<int> RunAsync(CancellationToken ct);

    /// Starts the plan's next round now ("Start now"). False when the plan is
    /// inactive or has no next intervention.
    Task<bool> StartNowAsync(Guid planId, CancellationToken ct);
}

/// v0.1.67 (phase 3) — a maintenance round: one campaign for the plan's
/// intervention, and for every member a NEW ticket (owner decision) built
/// from the plan (queue, optional ticket template, subject; `{{tokens}}` for
/// the member's contact and company) through <see cref="ISystemTicketCreator"/>,
/// added with the intervention moment as its proposed date. With auto-send
/// on, the round's mail goes out through the normal campaign flow as the
/// plan's sender — every send rule is rechecked for that user per mail, and
/// the optional Pending status lasts until the intervention. Otherwise the
/// campaign waits as a draft for an agent.
public sealed class MaintenanceRoundService : IMaintenanceRoundService
{
    private const int BatchSize = 10;
    public const string Origin = "maintenance-planner";

    private readonly IMaintenanceRoundRepository _rounds;
    private readonly ISystemTicketCreator _creator;
    private readonly ITicketTemplateRepository _templates;
    private readonly IComposeTokenResolver _tokens;
    private readonly ICampaignRepository _campaigns;
    private readonly ICampaignRecipientRepository _recipients;
    private readonly ICampaignService _campaignService;
    private readonly IUserService _users;
    private readonly ICampaignNotifier _notifier;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<MaintenanceRoundService> _logger;

    public MaintenanceRoundService(
        IMaintenanceRoundRepository rounds, ISystemTicketCreator creator, ITicketTemplateRepository templates,
        IComposeTokenResolver tokens, ICampaignRepository campaigns, ICampaignRecipientRepository recipients,
        ICampaignService campaignService, IUserService users, ICampaignNotifier notifier, ISettingsService settings,
        TimeProvider clock, ILogger<MaintenanceRoundService> logger)
    {
        _rounds = rounds;
        _creator = creator;
        _templates = templates;
        _tokens = tokens;
        _campaigns = campaigns;
        _recipients = recipients;
        _campaignService = campaignService;
        _users = users;
        _notifier = notifier;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!await _settings.GetOrAsync<bool>(SettingKeys.Campaigns.MaintenanceEnabled, true, ct)) return 0;
        var format = await CampaignFormat.LoadAsync(_settings, ct);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, format.Zone);
        await _rounds.StartDueRoundsAsync(nowLocal, BatchSize, ct);
        return await ProcessOpenAsync(format, ct);
    }

    public async Task<bool> StartNowAsync(Guid planId, CancellationToken ct)
    {
        if (await _rounds.StartNowAsync(planId, ct) is null) return false;
        await ProcessOpenAsync(await CampaignFormat.LoadAsync(_settings, ct), ct);
        return true;
    }

    private async Task<int> ProcessOpenAsync(CampaignFormat format, CancellationToken ct)
    {
        var open = await _rounds.ClaimOpenAsync(BatchSize, ct);
        foreach (var round in open)
        {
            try
            {
                await ProcessAsync(round, format, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Maintenance round {RoundId} (plan {PlanId}) failed, attempt {Attempt}.",
                    round.Id, round.PlanId, round.Attempts);
                var backoff = TimeSpan.FromMinutes(Math.Min(24 * 60, 15 * Math.Pow(2, round.Attempts - 1)));
                await _rounds.MarkFailedAsync(round.Id, ex.Message, _clock.GetUtcNow().UtcDateTime.Add(backoff), CancellationToken.None);
            }
        }
        return open.Count;
    }

    private async Task ProcessAsync(OpenMaintenanceRound round, CampaignFormat format, CancellationToken ct)
    {
        // Plan-level tokens are the same for the whole round: filled in on the
        // round's campaign; every other token is left for the campaign renderer.
        var mail = MaintenanceTokens.RenderPlanLevel(round.MailSubject, round.MailBodyHtml, round.PlanName, round.InterventionDate, format);
        var campaignId = round.CampaignId ?? await _campaigns.GetOrCreateForRoundAsync(round.Id,
            $"{round.PlanName} — {round.InterventionDate.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}",
            mail.Subject, mail.BodyHtml, round.OtherTimeNotice, ct);
        if (round.CampaignId is null) await _rounds.SetCampaignAsync(round.Id, campaignId, ct);

        var proposedUtc = MaintenanceCalendar.ProposedUtc(round.InterventionDate, round.InterventionMinute, format.Zone);
        var template = round.TicketTemplateId is { } tid ? await _templates.GetAsync(tid, ct) : null;
        var ticketIds = new List<Guid>();
        foreach (var member in await _rounds.MembersAsync(round.Id, round.PlanId, ct))
        {
            var ticketId = member.TicketId ?? await CreateTicketAsync(round, member, template, format, ct);
            ticketIds.Add(ticketId);
        }

        if (ticketIds.Count > 0)
        {
            await _recipients.AddAsync(campaignId, ticketIds, CampaignTicketSources.Maintenance, null,
                QueueAccessScope.AdminScope, TicketBulkActionService.CampaignHardMaxSelection, ct);
            await _recipients.SetProposedAsync(campaignId, ticketIds, proposedUtc, QueueAccessScope.AdminScope, ct);
        }

        if (round.AutoSend && ticketIds.Count > 0)
            await AutoSendAsync(round, campaignId, proposedUtc, ct);

        await _rounds.MarkCreatedAsync(round.Id, ct);
        await _notifier.CampaignChangedAsync(campaignId, ct);
    }

    private async Task<Guid> CreateTicketAsync(OpenMaintenanceRound round, RoundMember member,
        Domain.TicketTemplates.TicketTemplate? template, CampaignFormat format, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(
            await _tokens.ResolveForContactAsync(member.ContactId, member.CompanyId, agentEmail: null, ct), StringComparer.Ordinal)
        {
            [MaintenanceTokens.PlanName] = round.PlanName,
            [MaintenanceTokens.MemberLabel] = member.Label,
            [MaintenanceTokens.InterventionDate] = round.InterventionDate.ToString(format.DateFormat, format.Culture),
        };

        var subjectTemplate = !string.IsNullOrWhiteSpace(template?.Subject) ? template!.Subject
            : !string.IsNullOrWhiteSpace(round.TicketSubject) ? round.TicketSubject
            : string.IsNullOrWhiteSpace(member.Label) ? MaintenanceTokens.PlanName
            : $"{MaintenanceTokens.PlanName} — {MaintenanceTokens.MemberLabel}";
        var subject = ComposeTokenRenderer.Render(MaintenanceTokens.DropEmptyLabel(subjectTemplate, member.Label), map, ComposeTokenEscape.PlainText).Output;
        var body = ComposeTokenRenderer.Render(MaintenanceTokens.DropEmptyLabel(template?.BodyHtml, member.Label), map, ComposeTokenEscape.Html).Output;
        var note = string.IsNullOrWhiteSpace(template?.InitialNoteHtml)
            ? null
            : new Persistence.Tickets.InitialTicketNote(
                ComposeTokenRenderer.Render(MaintenanceTokens.DropEmptyLabel(template!.InitialNoteHtml, member.Label), map, ComposeTokenEscape.Html).Output, template.InitialNoteInternal);

        var ticket = await _creator.CreateAsync(new NewSystemTicket(
            Subject: subject,
            BodyHtml: body,
            RequesterContactId: member.ContactId,
            CompanyId: member.CompanyId,
            QueueId: round.QueueId,
            StatusId: template?.StatusId,
            PriorityId: template?.PriorityId,
            CategoryId: template?.CategoryId,
            AssigneeUserId: template?.AssigneeUserId,
            TicketTypeId: template?.TicketTypeId,
            InitialNote: note), Origin, ct)
            ?? throw new InvalidOperationException("No active status or priority is configured; cannot create the maintenance tickets.");

        await _rounds.RecordTicketAsync(round.Id, member.MemberId, ticket.Id, ct);
        return ticket.Id;
    }

    /// Sends the round's campaign as the plan's sender through the normal
    /// campaign flow. A sender who lost access, an incomplete mail or "every
    /// ticket was already mailed" (a retry) leaves the campaign as a draft —
    /// never forced; the reason lands on the round.
    private async Task AutoSendAsync(OpenMaintenanceRound round, Guid campaignId, DateTime proposedUtc, CancellationToken ct)
    {
        var actor = await MaintenanceSender.ResolveAsync(_users, round.SendAsUserId, ct);
        var pendingTill = MaintenanceCalendar.PendingTill(round.PendingStatusId is not null, round.PendingDays, proposedUtc, _clock.GetUtcNow().UtcDateTime);
        try
        {
            await _campaignService.StartRoundAsync(campaignId, actor, new CampaignRoundRequest(
                CampaignRoundKinds.Initial,
                pendingTill is null ? null : round.PendingStatusId,
                pendingTill), ct);
        }
        catch (CampaignRejectedException ex) when (ex.Code == "nothing_to_send")
        {
            // A retry after the mails were queued: nothing left to do.
        }
    }
}
