using Servicedesk.Infrastructure.Campaigns;

namespace Servicedesk.Api.Campaigns;

public sealed record CreateCampaignRequest(
    string? Name,
    string? Subject,
    string? BodyHtml,
    Guid? SourceTemplateId,
    Guid? ProjectTicketId,
    DateTime? DefaultProposedUtc,
    bool? AddProjectTickets,
    IReadOnlyList<Guid>? TicketIds,
    string? OtherTimeNotice = null);

public sealed record UpdateCampaignRequest(
    string? Name,
    string? Subject,
    string? BodyHtml,
    Guid? SourceTemplateId,
    Guid? ProjectTicketId,
    bool? ClearProject,
    DateTime? DefaultProposedUtc,
    bool? ClearDefaultProposed,
    string? State,
    string? OtherTimeNotice = null);

public sealed record DuplicateCampaignRequest(string? Name);

public sealed record CampaignTicketIdsRequest(IReadOnlyList<Guid>? TicketIds);

public sealed record CampaignProposedRequest(IReadOnlyList<Guid>? TicketIds, DateTime? ProposedUtc);

public sealed record CampaignDecisionRequest(string? Decision);

public sealed record CampaignPlanRequest(string? Kind, IReadOnlyList<Guid>? TicketIds);

public sealed record StartCampaignRoundRequest(
    string? Kind,
    Guid? PendingStatusId,
    DateTime? PendingTillUtc,
    IReadOnlyList<Guid>? TicketIds,
    DateTime? ScheduledUtc = null);

/// Bounds and response shaping shared by the campaign endpoint groups.
internal static class CampaignApi
{
    public const int MaxNameLength = 200;
    public const int MaxSubjectLength = 500;
    public const int MaxBodyLength = 200_000;
    /// Absolute bound on one request's id list (the setting caps the campaign).
    public const int MaxTicketIds = 2000;

    public static IResult Rejected(CampaignRejectedException ex) => ex.Code switch
    {
        "not_found" => Results.NotFound(),
        "archived" or "nothing_to_send" or "all_skipped" or "campaigns_disabled" =>
            Results.Conflict(new { error = ex.Message, code = ex.Code }),
        _ => Results.BadRequest(new { error = ex.Message, code = ex.Code }),
    };

    public static object MapRecipient(CampaignRecipientView v) => new
    {
        ticketId = v.Row.TicketId,
        number = v.Row.Number,
        subject = v.Row.Subject,
        statusName = v.Row.StatusName,
        stateCategory = v.Row.StateCategory,
        isProject = v.Row.IsProject,
        isMerged = v.Row.IsMerged,
        pendingTillUtc = v.Row.PendingTillUtc,
        requesterName = v.Row.RequesterName,
        requesterEmail = v.Row.RequesterEmail,
        companyName = v.Row.CompanyName,
        proposedUtc = v.Row.ProposedUtc,
        source = v.Row.Source,
        addedUtc = v.Row.AddedUtc,
        decision = v.Row.Decision,
        decisionUtc = v.Row.DecisionUtc,
        decisionByEmail = v.Row.DecisionByEmail,
        decisionSource = v.Row.DecisionSource,
        decisionNote = v.Row.DecisionNote,
        decisionPreferredDate = v.Row.DecisionPreferredDate?.ToString("yyyy-MM-dd"),
        decisionPreferredMinute = v.Row.DecisionPreferredMinute,
        notBeforeUtc = v.Row.NotBeforeUtc,
        answerLinkExpiresUtc = v.Row.AnswerLinkExpiresUtc,
        messageState = v.Row.MessageState,
        skipReason = v.Row.SkipReason,
        lastError = v.Row.LastError,
        pendingResult = v.Row.PendingResult,
        roundNo = v.Row.RoundNo,
        firstSentUtc = v.Row.FirstSentUtc,
        lastSentUtc = v.Row.LastSentUtc,
        roundPendingTillUtc = v.Row.RoundPendingTillUtc,
        replyUtc = v.Row.ReplyUtc,
        replyType = v.Row.ReplyType,
        state = v.State,
    };

    public static object MapPlanItem(CampaignPlanItem p, bool includeBody) => new
    {
        ticketId = p.TicketId,
        number = p.Number,
        ticketSubject = p.TicketSubject,
        requesterName = p.RequesterName,
        requesterEmail = p.RequesterEmail,
        companyName = p.CompanyName,
        proposedUtc = p.ProposedUtc,
        skipReason = p.SkipReason,
        missingTokens = p.MissingTokens,
        renderedSubject = p.RenderedSubject,
        renderedBodyHtml = includeBody ? p.RenderedBodyHtml : null,
    };

    public static object MapRound(CampaignRoundRow r) => new
    {
        id = r.Id,
        roundNo = r.RoundNo,
        kind = r.Kind,
        subject = r.Subject,
        sentByEmail = r.SentByEmail,
        pendingStatusId = r.PendingStatusId,
        pendingTillUtc = r.PendingTillUtc,
        scheduledUtc = r.ScheduledUtc,
        startedUtc = r.StartedUtc,
        completedUtc = r.CompletedUtc,
        total = r.Total,
        sent = r.Sent,
        queued = r.Queued,
        failed = r.Failed,
        skipped = r.Skipped,
        cancelled = r.Cancelled,
    };

    public static string? ValidateIds(IReadOnlyList<Guid>? ids, bool required = true)
    {
        if (ids is null || ids.Count == 0) return required ? "ticketIds is required." : null;
        if (ids.Count > MaxTicketIds) return $"At most {MaxTicketIds} tickets per request.";
        return null;
    }
}
