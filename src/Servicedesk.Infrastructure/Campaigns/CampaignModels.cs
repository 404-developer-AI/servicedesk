namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — Campaigns: one mail per ticket to many tickets at once
/// (ARCHITECTURE.md → Campaigns). Stable string codes; the UI maps them to
/// copy and the database CHECK constraints list the same values.
public static class CampaignStates
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Archived = "Archived";

    public static bool IsValid(string? value) => value is Draft or Active or Archived;
}

public static class CampaignMessageStates
{
    public const string Queued = "Queued";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string Cancelled = "Cancelled";
}

public static class CampaignDecisionSources
{
    public const string Agent = "Agent";
    public const string Customer = "Customer";
}

public static class CampaignDecisions
{
    public const string Approved = "Approved";
    public const string OtherTime = "OtherTime";
    public const string Declined = "Declined";

    public static bool IsValid(string? value) => value is Approved or OtherTime or Declined;
}

public static class CampaignRoundKinds
{
    public const string Initial = "Initial";
    public const string FollowUp = "FollowUp";
}

public static class CampaignTicketSources
{
    public const string Selection = "Selection";
    public const string Project = "Project";
    /// Phase 3 — added by the maintenance planner.
    public const string Maintenance = "Maintenance";
}

/// Why a ticket is not mailed. Checked when a round is queued and again
/// right before each send (the ticket may have changed in between).
public static class CampaignSkipReasons
{
    public const string NoAccess = "no_access";
    public const string ProjectTicket = "project_ticket";
    public const string Closed = "closed";
    public const string Merged = "merged";
    public const string NoRequesterEmail = "no_requester_email";
    public const string MissingTokens = "missing_tokens";
    public const string SenderNotAllowed = "sender_not_allowed";
    public const string NoMailbox = "no_mailbox";
    public const string RequesterChanged = "requester_changed";
    /// Phase 2 — the message uses the answer link but App.PublicBaseUrl is unset.
    public const string AnswerLinkUnavailable = "answer_link_unavailable";
    /// Phase 2 — the message uses the answer link and the proposed date has passed.
    public const string ProposedDatePassed = "proposed_date_passed";
    public const string Invalid = "invalid";
}

/// The derived per-ticket state on the campaign dashboard.
public static class CampaignRecipientStates
{
    public const string NotSent = "NotSent";
    public const string Queued = "Queued";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string ReplyReceived = "ReplyReceived";
    public const string TimerExpired = "TimerExpired";
    public const string Waiting = "Waiting";
}

public sealed class CampaignRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public string OtherTimeNotice { get; set; } = string.Empty;
    public Guid? SourceTemplateId { get; set; }
    public Guid? ProjectTicketId { get; set; }
    public long? ProjectTicketNumber { get; set; }
    public string? ProjectTicketSubject { get; set; }
    public DateTime? DefaultProposedUtc { get; set; }
    public string State { get; set; } = CampaignStates.Draft;
    public Guid? DuplicatedFromId { get; set; }
    public Guid? MaintenancePlanId { get; set; }
    public string? MaintenancePlanName { get; set; }
    public Guid? MaintenanceRoundId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByEmail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CampaignListItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public Guid? ProjectTicketId { get; set; }
    public long? ProjectTicketNumber { get; set; }
    public string? CreatedByEmail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int TicketCount { get; set; }
    public int SentCount { get; set; }
    public int QueuedCount { get; set; }
    public int FailedCount { get; set; }
    public DateTime? LastSentUtc { get; set; }
}

public sealed class CampaignRoundRow
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public int RoundNo { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public Guid? SentByUserId { get; set; }
    public string? SentByEmail { get; set; }
    public string SentByRole { get; set; } = string.Empty;
    public Guid? PendingStatusId { get; set; }
    public DateTime? PendingTillUtc { get; set; }
    public DateTime? ScheduledUtc { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int Total { get; set; }
    public int Sent { get; set; }
    public int Queued { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Cancelled { get; set; }
}

/// One recipient as the dashboard reads it: the ticket, its requester, the
/// latest outbox row for this campaign and the first customer answer after
/// the first successful send. Only rows in the caller's queues.
public sealed class CampaignRecipientRow
{
    public Guid TicketId { get; set; }
    public long Number { get; set; }
    public string Subject { get; set; } = string.Empty;
    public Guid QueueId { get; set; }
    public bool IsProject { get; set; }
    public bool IsMerged { get; set; }
    public string StatusName { get; set; } = string.Empty;
    public string StateCategory { get; set; } = string.Empty;
    public DateTime? PendingTillUtc { get; set; }
    public string? RequesterEmail { get; set; }
    public string? RequesterName { get; set; }
    public string? CompanyName { get; set; }
    public DateTime? ProposedUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime AddedUtc { get; set; }
    public string? Decision { get; set; }
    public DateTime? DecisionUtc { get; set; }
    public string? DecisionByEmail { get; set; }
    public string? DecisionSource { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime? DecisionPreferredDate { get; set; }
    public int? DecisionPreferredMinute { get; set; }
    public string? MessageState { get; set; }
    public string? SkipReason { get; set; }
    public string? LastError { get; set; }
    public string? PendingResult { get; set; }
    public DateTime? NotBeforeUtc { get; set; }
    public DateTime? AnswerLinkExpiresUtc { get; set; }
    public int? RoundNo { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public DateTime? FirstSentUtc { get; set; }
    public DateTime? RoundPendingTillUtc { get; set; }
    public DateTime? ReplyUtc { get; set; }
    public string? ReplyType { get; set; }
}

public sealed record CampaignDashboard(
    IReadOnlyList<CampaignRecipientView> Recipients,
    int HiddenCount);

/// A recipient plus its derived state.
public sealed record CampaignRecipientView(CampaignRecipientRow Row, string State);
