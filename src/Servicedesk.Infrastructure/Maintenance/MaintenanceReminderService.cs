using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Campaigns;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Maintenance;

public interface IMaintenanceReminderService
{
    /// Sends the due reminders. Returns how many rounds were handled.
    Task<int> RunAsync(CancellationToken ct);
}

/// v0.1.67 (phase 3) — the maintenance reminder (owner decisions: once per
/// round, auto-send plans only, own subject + message). N days after the
/// round's first mail it starts a campaign FollowUp round through the normal
/// <see cref="ICampaignService"/> — so it reaches only recipients without a
/// mail reply and without a decision (customer answer link or agent), under
/// every single-ticket mail rule, as the plan's sender (rechecked). Nothing
/// is sent once the intervention moment has passed; the optional pending
/// status is applied again from the reminder on.
public sealed class MaintenanceReminderService : IMaintenanceReminderService
{
    private const int BatchSize = 10;

    private readonly IMaintenanceReminderRepository _reminders;
    private readonly ICampaignService _campaignService;
    private readonly IUserService _users;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<MaintenanceReminderService> _logger;

    public MaintenanceReminderService(IMaintenanceReminderRepository reminders, ICampaignService campaignService,
        IUserService users, ISettingsService settings, TimeProvider clock, ILogger<MaintenanceReminderService> logger)
    {
        _reminders = reminders;
        _campaignService = campaignService;
        _users = users;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!await _settings.GetOrAsync<bool>(SettingKeys.Campaigns.MaintenanceEnabled, true, ct)) return 0;
        var due = await _reminders.ClaimDueAsync(_clock.GetUtcNow().UtcDateTime, BatchSize, ct);
        if (due.Count == 0) return 0;

        var format = await CampaignFormat.LoadAsync(_settings, ct);
        foreach (var reminder in due)
        {
            string note;
            try
            {
                note = await SendAsync(reminder, format, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Claimed = once: the reason is shown on the plan, never retried or forced.
                _logger.LogWarning(ex, "Maintenance reminder for round {RoundId} was not sent.", reminder.RoundId);
                note = $"Reminder not sent: {ex.Message}";
            }
            await _reminders.SetNoteAsync(reminder.RoundId, note, CancellationToken.None);
        }
        return due.Count;
    }

    private async Task<string> SendAsync(DueMaintenanceReminder reminder, CampaignFormat format, CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var interventionUtc = MaintenanceCalendar.ProposedUtc(reminder.InterventionDate, reminder.InterventionMinute, format.Zone);
        if (interventionUtc <= nowUtc) return "Reminder not sent: the intervention moment had passed.";

        var actor = await MaintenanceSender.ResolveAsync(_users, reminder.SendAsUserId, ct);
        var mail = MaintenanceTokens.RenderPlanLevel(reminder.ReminderSubject, reminder.ReminderBodyHtml,
            reminder.PlanName, reminder.InterventionDate, format);
        var pendingTill = MaintenanceCalendar.PendingTill(reminder.PendingStatusId is not null, reminder.PendingDays, interventionUtc, nowUtc);
        try
        {
            var started = await _campaignService.StartRoundAsync(reminder.CampaignId, actor, new CampaignRoundRequest(
                CampaignRoundKinds.FollowUp,
                pendingTill is null ? null : reminder.PendingStatusId,
                pendingTill,
                SubjectOverride: mail.Subject,
                BodyHtmlOverride: mail.BodyHtml), ct);
            return started.Skipped == 0
                ? $"Reminder sent to {started.Queued} customer{(started.Queued == 1 ? "" : "s")}."
                : $"Reminder sent to {started.Queued}; {started.Skipped} skipped (see the campaign).";
        }
        catch (CampaignRejectedException ex) when (ex.Code == "nothing_to_send")
        {
            return "No reminder needed: every customer had answered.";
        }
    }
}
