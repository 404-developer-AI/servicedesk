using System.Text.Json;
using Servicedesk.Domain.Surveys;

namespace Servicedesk.Infrastructure.Surveys;

/// Per-send token-protected invitations + their submissions. State
/// transitions (Sent → Submitted, Sent → Expired, Sent → Cancelled) are
/// atomic: each one writes the matching ticket-event in the same
/// transaction as the status flip so the agent-side timeline never shows a
/// half-applied transition.
public interface ISurveyInvitationRepository
{
    /// Atomically inserts a Sent invitation + writes the SurveySent ticket
    /// event in one transaction. Returns null when the active-pair partial
    /// unique index trips (an active invitation already exists for this
    /// (survey, ticket) pair) — the caller maps that to a silent skip so
    /// repeated trigger-evaluations don't spam the customer.
    Task<SurveyInvitationCreated?> CreateSentAsync(
        Guid surveyId,
        Guid ticketId,
        byte[] tokenHash,
        byte[] tokenCipher,
        string sentToEmail,
        DateTime expiresUtc,
        IReadOnlyList<Guid> attributedAgentIds,
        string surveySnapshotJson,
        Guid? createdBy,
        string sentEventMetadataJson,
        CancellationToken ct);

    /// Per-ticket listing for the agent-side timeline (drawer / sidebar).
    Task<IReadOnlyList<SurveyInvitationSummary>> ListForTicketAsync(Guid ticketId, CancellationToken ct);

    /// Per-survey listing for the resultsetup overview page. Pagination
    /// uses keyset on (sent_utc desc, id) — caller passes the cursor from
    /// the previous page, or null for the first page.
    Task<IReadOnlyList<SurveyInvitationSummary>> ListForSurveyAsync(
        Guid surveyId,
        SurveyInvitationStatus? statusFilter,
        int limit,
        CancellationToken ct);

    Task<bool> ActiveExistsAsync(Guid surveyId, Guid ticketId, CancellationToken ct);

    Task<int> CountForSurveyAsync(Guid surveyId, SurveyInvitationStatus? statusFilter, CancellationToken ct);

    /// Public path. Token validation is hash-based; returns null for unknown
    /// tokens AND for Cancelled rows — the public endpoint 404s both to
    /// avoid leaking whether a token ever existed. Does NOT filter by
    /// expiry; the public page renders an "expired" pane when
    /// <see cref="SurveyPublicView.Status"/> is <c>Expired</c> or the
    /// invitation's <c>ExpiresUtc</c> has passed.
    Task<SurveyPublicView?> GetByTokenHashForPublicAsync(byte[] tokenHash, CancellationToken ct);

    /// Atomic submit: re-checks status=Sent + not-expired under a row lock,
    /// inserts the response + answers (Survey-scope + per-agent), writes
    /// SurveySubmitted ticket-event (carve-out: this event MUST NOT trigger
    /// auto-reopen — the response service skips the trigger evaluator that
    /// mail-ingest runs). Returns null on race (already submitted, expired,
    /// or cancelled between GET and POST).
    Task<SurveySubmitResult?> TrySubmitAsync(
        byte[] tokenHash,
        SurveySubmitInput input,
        string? ip,
        string? userAgent,
        DateTime nowUtc,
        CancellationToken ct);

    /// Flips Sent → Expired for past-due rows and writes a SurveyExpired
    /// event per ticket. Returns the touched ids so the caller can
    /// broadcast SignalR invalidations.
    Task<IReadOnlyList<SurveyExpiredInstance>> ExpireStaleAsync(int maxBatch, DateTime nowUtc, CancellationToken ct);

    /// Cancel a Sent invitation (admin-side; used when an admin wants to
    /// retire the link without waiting for expiry). Idempotent: returns
    /// false when the row is no longer Sent.
    Task<bool> CancelSentAsync(Guid invitationId, CancellationToken ct);

    /// Aggregate results for the survey-overview page.
    Task<SurveyAggregateResults> GetAggregateResultsAsync(Guid surveyId, CancellationToken ct);

    /// Drill-down to a single response (rendered against the invitation's
    /// frozen survey snapshot).
    Task<SurveyResponseDetail?> GetResponseDetailAsync(Guid invitationId, CancellationToken ct);
}

public sealed record SurveyInvitationCreated(Guid InvitationId, long SentEventId);

public sealed record SurveyInvitationSummary(
    Guid Id,
    Guid SurveyId,
    string SurveyName,
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    SurveyInvitationStatus Status,
    string SentToEmail,
    DateTime SentUtc,
    DateTime ExpiresUtc,
    DateTime? SubmittedUtc,
    /// Display name of the ticket's requester contact (first + last, falling
    /// back to email). Null when the contact row is missing.
    string? ContactName,
    /// Name of the requester contact's primary company (via
    /// contact_companies.role='primary'). Null when the contact has no
    /// primary-role company link.
    string? CompanyName);

public sealed record SurveyPublicView(
    Guid InvitationId,
    Guid SurveyId,
    string SurveyName,
    string IntroHtml,
    /// Heading shown above the per-agent block. Null/empty = no heading
    /// rendered.
    string? AgentBlockHeading,
    string SubmitButtonLabel,
    string ThankYouMessage,
    string ExpiredMessage,
    string NotFoundMessage,
    SurveyInvitationStatus Status,
    DateTime ExpiresUtc,
    /// Frozen at send time. Each row carries the agent's display name and
    /// id so the public page can render rating-blocks even after the agent
    /// changes their profile.
    IReadOnlyList<SurveyAttributedAgent> AttributedAgents,
    /// Survey-scoped questions (asked once). Resolved from the invitation's
    /// snapshot — never the live <c>survey_questions</c> table — so admin
    /// edits don't reshape what's already in the customer's inbox.
    IReadOnlyList<SurveyQuestion> Questions,
    /// Agent-scoped questions (rendered once per attributed agent). Same
    /// snapshot-locked semantics as <see cref="Questions"/>.
    IReadOnlyList<SurveyQuestion> AgentQuestions);

public sealed record SurveyAttributedAgent(Guid UserId, string DisplayName);

public sealed record SurveySubmitInput(
    string? Comment,
    /// Answers to Survey-scoped questions. One entry per answered question.
    IReadOnlyList<SurveySubmitAnswer> Answers,
    /// Answers to Agent-scoped questions. One entry per
    /// (agent, question) pair the customer filled in.
    IReadOnlyList<SurveySubmitAgentAnswer> AgentAnswers);

public sealed record SurveySubmitAnswer(
    long QuestionId,
    decimal? ValueNumeric,
    string? ValueText,
    /// Stringified JSON for multi-choice answers. Null for other types.
    string? ValueJson);

public sealed record SurveySubmitAgentAnswer(
    Guid AgentUserId,
    long QuestionId,
    decimal? ValueNumeric,
    string? ValueText,
    string? ValueJson);

public sealed record SurveySubmitResult(
    Guid InvitationId,
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    Guid SurveyId,
    string SurveyName,
    long SubmittedEventId,
    /// Agents to notify on submission (the attributed-agent list, since
    /// every contributing agent received at least one sub-question).
    /// Used by the response service to call <c>IUserNotifier</c> outside
    /// the transaction.
    IReadOnlyList<Guid> AgentUserIdsToNotify);

public sealed record SurveyExpiredInstance(Guid InvitationId, Guid TicketId, Guid SurveyId, string SurveyName, long ExpiredEventId);

public sealed record SurveyAggregateResults(
    Guid SurveyId,
    int TotalSent,
    int TotalSubmitted,
    int TotalExpired,
    int TotalCancelled,
    IReadOnlyList<SurveyAgentLeaderboardRow> AgentLeaderboard,
    /// Aggregates for Survey-scope questions.
    IReadOnlyList<SurveyQuestionAggregate> QuestionAggregates,
    /// Aggregates for Agent-scope questions, broken down per (question,
    /// agent). The agent leaderboard surfaces totals; this list powers the
    /// detailed per-agent question breakdown.
    IReadOnlyList<SurveyAgentQuestionAggregate> AgentQuestionAggregates);

public sealed record SurveyAgentLeaderboardRow(
    Guid AgentUserId,
    string DisplayName,
    /// Number of submissions that touched this agent at all (any
    /// Agent-scope answer with this agent_user_id).
    int ResponseCount,
    /// Average across all numeric (Rating/Nps) Agent-scope answers for this
    /// agent. Null when no numeric sub-questions exist for the survey.
    decimal? AverageRating);

public sealed record SurveyQuestionAggregate(
    long QuestionId,
    string Label,
    SurveyQuestionType Type,
    int AnswerCount,
    decimal? AverageNumeric,
    /// For SingleChoice/MultiChoice: tally per option value. For Rating/Nps
    /// the same tally is used to render a histogram (key = numeric value).
    /// Null for free-text questions.
    JsonDocument? Tally);

public sealed record SurveyAgentQuestionAggregate(
    long QuestionId,
    string Label,
    SurveyQuestionType Type,
    Guid AgentUserId,
    string AgentDisplayName,
    int AnswerCount,
    decimal? AverageNumeric,
    JsonDocument? Tally);

public sealed record SurveyResponseDetail(
    Guid InvitationId,
    Guid TicketId,
    long TicketNumber,
    string TicketSubject,
    string SentToEmail,
    DateTime SentUtc,
    DateTime SubmittedUtc,
    string? Comment,
    JsonDocument SurveySnapshot,
    /// Survey-scope answers (AgentUserId always null).
    IReadOnlyList<SurveyAnswerView> Answers,
    /// Agent-scope answers, one per (agent, question) pair the customer
    /// filled in.
    IReadOnlyList<SurveyAgentAnswerView> AgentAnswers);

public sealed record SurveyAnswerView(
    long QuestionId,
    decimal? ValueNumeric,
    string? ValueText,
    JsonDocument? ValueJson);

public sealed record SurveyAgentAnswerView(
    Guid AgentUserId,
    string AgentDisplayName,
    long QuestionId,
    decimal? ValueNumeric,
    string? ValueText,
    JsonDocument? ValueJson);
