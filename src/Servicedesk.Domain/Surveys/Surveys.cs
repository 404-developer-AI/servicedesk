using System.Text.Json;

namespace Servicedesk.Domain.Surveys;

/// A survey definition built in the admin designer. Surveys are not directly
/// sent — they're materialised into <see cref="SurveyInvitation"/> rows via
/// either the <c>send_survey</c> trigger action or the linked-survey field on
/// a compose template.
///
/// <para><see cref="TtlDays"/> overrides the global default
/// (<c>SettingKeys.Surveys.DefaultTtlDays</c>) for invitations minted from
/// this survey. <c>null</c> means "use the global default".</para>
/// <para>The five label fields hold every line of text the public survey
/// renders. They are admin-supplied (no built-in English defaults) so a
/// Dutch-speaking customer never sees a fallback string.</para>
public sealed record Survey(
    Guid Id,
    string Name,
    string? Description,
    /// Intro paragraph shown on the public survey page above the questions.
    /// Sanitized HTML; supports the standard compose-token placeholders.
    string IntroHtml,
    /// Subject line of the invitation email.
    string InviteSubject,
    /// Body of the invitation email. Sanitized HTML; supports compose tokens
    /// plus the new <c>{{survey.link}}</c> and <c>{{ticket.agentNames}}</c>
    /// placeholders resolved at send time.
    string InviteBodyHtml,
    bool IsActive,
    int? TtlDays,
    /// Heading shown above the per-agent sub-question block on the public
    /// page. Empty = no heading rendered (the block still appears if any
    /// Agent-scoped questions exist).
    string? AgentBlockHeading,
    /// Text on the "Submit" button on the public page. Required at save
    /// time so the customer never sees a fallback English label.
    string SubmitButtonLabel,
    /// Body shown on the confirmation screen after a successful submission.
    string ThankYouMessage,
    /// Body shown on the public page when the invitation has expired.
    string ExpiredMessage,
    /// Body shown on the public page when the token is invalid or unknown.
    string NotFoundMessage,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedBy,
    IReadOnlyList<SurveyQuestion> Questions);

public sealed record SurveyQuestion(
    long Id,
    Guid SurveyId,
    int SortOrder,
    SurveyQuestionType Type,
    /// Where this question renders. <see cref="SurveyQuestionScope.Survey"/>
    /// = asked once; <see cref="SurveyQuestionScope.Agent"/> = repeated for
    /// every contributing agent on the ticket.
    SurveyQuestionScope AppliesTo,
    string Label,
    string? HelpText,
    bool IsRequired,
    /// Type-specific config. For <see cref="SurveyQuestionType.Rating"/> this
    /// is <c>{ "points": int, "labels": string[]? }</c>; for choice types it's
    /// <c>{ "options": [{"value","label"}, ...] }</c>. Always a valid JSON
    /// object — never null — even for types that need no config.
    JsonDocument Config);

/// Per-send token-protected row. Spans the entire lifecycle of one survey
/// link from mint through submit/expire/cancel. Modelled after
/// <c>IntakeFormInstance</c>.
public sealed record SurveyInvitation(
    Guid Id,
    Guid SurveyId,
    Guid TicketId,
    long? SentEventId,
    long? SubmittedEventId,
    SurveyInvitationStatus Status,
    string SentToEmail,
    DateTime SentUtc,
    DateTime ExpiresUtc,
    DateTime? SubmittedUtc,
    DateTime? CancelledUtc,
    /// Snapshot of contributing agent user-ids captured at send time. The
    /// public survey page renders the per-agent sub-question block once per
    /// id; reassignments after send do not retroactively re-shape the list.
    IReadOnlyList<Guid> AttributedAgentUserIds,
    /// Frozen survey definition (name, intro, label fields, both question
    /// lists) so a live admin edit does not corrupt pending invitations.
    JsonDocument SurveySnapshot,
    Guid? CreatedBy);

public sealed record SurveyResponse(
    Guid Id,
    Guid InvitationId,
    DateTime SubmittedUtc,
    string? Comment,
    IReadOnlyList<SurveyAnswer> Answers);

public sealed record SurveyAnswer(
    long Id,
    Guid ResponseId,
    long QuestionId,
    /// When the answer is to an Agent-scoped question, this carries the
    /// attributed agent's user id; one row per (question, agent) pair.
    /// NULL for Survey-scoped answers.
    Guid? AgentUserId,
    /// One of the three value-shape columns is filled per question type:
    /// numeric for Rating/Nps, text for free Text, json (string array) for
    /// MultiChoice; SingleChoice stores the chosen value in <c>ValueText</c>.
    decimal? ValueNumeric,
    string? ValueText,
    JsonDocument? ValueJson);

/// Placeholders the survey-aware token resolver injects on top of the
/// standard compose-tokens. Reusable across invite subject + body + intro
/// HTML at send time.
public static class SurveyTokens
{
    public const string SurveyLink = "{{survey.link}}";
    public const string TicketAgentNames = "{{ticket.agentNames}}";

    public static readonly IReadOnlyList<string> Supported = new[]
    {
        SurveyLink,
        TicketAgentNames,
    };
}
