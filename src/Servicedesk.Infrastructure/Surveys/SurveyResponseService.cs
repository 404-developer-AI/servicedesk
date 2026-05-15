using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Surveys;

/// Orchestrates a public survey submission: persists the response via the
/// repo (atomic), then fans notifications out to rated agents.
///
/// <para><b>Carve-out:</b> this service deliberately does NOT call the
/// trigger evaluator. The SurveySubmitted ticket-event lands but the auto-
/// reopen rules don't fire — that's the hard requirement from the spec.
/// Compare to <c>MailIngestService</c> which runs the evaluator on every
/// inbound message.</para>
public interface ISurveyResponseService
{
    /// Returns null on race (already submitted / expired between GET and
    /// POST), otherwise the persisted result.
    Task<SurveySubmitResult?> SubmitAsync(
        byte[] tokenHash,
        SurveySubmitInput input,
        string? ip,
        string? userAgent,
        CancellationToken ct);
}

public sealed class SurveyResponseService : ISurveyResponseService
{
    private readonly ISurveyInvitationRepository _repo;
    private readonly INotificationRepository _notifications;
    private readonly IUserNotifier _notifier;
    private readonly ISettingsService _settings;
    private readonly ITicketListNotifier _listNotifier;
    private readonly ILogger<SurveyResponseService> _logger;

    public SurveyResponseService(
        ISurveyInvitationRepository repo,
        INotificationRepository notifications,
        IUserNotifier notifier,
        ISettingsService settings,
        ITicketListNotifier listNotifier,
        ILogger<SurveyResponseService> logger)
    {
        _repo = repo;
        _notifications = notifications;
        _notifier = notifier;
        _settings = settings;
        _listNotifier = listNotifier;
        _logger = logger;
    }

    public async Task<SurveySubmitResult?> SubmitAsync(
        byte[] tokenHash,
        SurveySubmitInput input,
        string? ip,
        string? userAgent,
        CancellationToken ct)
    {
        var result = await _repo.TrySubmitAsync(tokenHash, input, ip, userAgent, DateTime.UtcNow, ct);
        if (result is null) return null;

        try
        {
            // Light-weight invalidation so any agent looking at the ticket's
            // timeline sees the new event without a manual refresh.
            await _listNotifier.NotifyUpdatedAsync(result.TicketId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SurveyResponseService: failed to broadcast TicketUpdated for {TicketId}.",
                result.TicketId);
        }

        // Agent notifications: gated by setting. Survey responses produce
        // their own NotificationType ('survey_submitted') so the frontend
        // can render a distinct icon/colour from a @-mention.
        var notifyEnabled = await _settings.GetAsync<bool>(SettingKeys.Surveys.EnableAgentNotifications, ct);
        if (!notifyEnabled || result.AgentUserIdsToNotify.Count == 0) return result;

        var preview = BuildPreview(result.SurveyName, input.Comment);

        var rows = result.AgentUserIdsToNotify
            .Distinct()
            .Select(uid => new NewUserNotification(
                UserId: uid,
                SourceUserId: null,
                NotificationType: "survey_submitted",
                TicketId: result.TicketId,
                TicketNumber: result.TicketNumber,
                TicketSubject: $"{result.SurveyName} — #{result.TicketNumber} {result.TicketSubject}",
                EventId: result.SubmittedEventId,
                EventType: "SurveySubmitted",
                PreviewText: preview))
            .ToList();

        try
        {
            var inserted = await _notifications.CreateManyAsync(rows, ct);
            foreach (var row in inserted)
            {
                var payload = new UserNotificationPush(
                    Id: row.Id,
                    TicketId: row.TicketId,
                    TicketNumber: row.TicketNumber,
                    TicketSubject: row.TicketSubject,
                    SourceUserEmail: null,
                    EventId: row.EventId,
                    EventType: row.EventType,
                    PreviewText: row.PreviewText,
                    CreatedUtc: row.CreatedUtc);
                try
                {
                    await _notifier.NotifyMentionAsync(row.UserId, payload, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Survey notification push failed for {NotificationId} (user {UserId}).",
                        row.Id, row.UserId);
                }
            }
        }
        catch (Exception ex)
        {
            // The submission itself committed — losing notification rows is
            // strictly better than losing the response.
            _logger.LogError(ex,
                "Failed to persist survey notifications for ticket {TicketId} event {EventId}.",
                result.TicketId, result.SubmittedEventId);
        }

        return result;
    }

    private static string BuildPreview(string surveyName, string? comment)
    {
        // Notification body shown in the bell-dropdown. With the new
        // per-agent-question model there's no single "overall rating" to
        // surface here — the rated agents click through for the full
        // breakdown.
        var commentPart = string.IsNullOrWhiteSpace(comment) ? "(no comment)" : Truncate(comment.Trim(), 180);
        var full = $"{surveyName}: {commentPart}";
        return full.Length <= 200 ? full : full.Substring(0, 199) + "…";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
