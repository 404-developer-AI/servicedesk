namespace Servicedesk.Domain.Surveys;

/// Question types supported in v0.0.38 surveys. Serialised member name is the
/// value persisted in `survey_questions.question_type` and is kept in sync
/// with the `chk_survey_question_type` DB CHECK constraint.
///
/// <para><see cref="Rating"/> renders as a configurable-point scale (3, 5,
/// 10 …) with optional per-point text labels (e.g. "Bad / OK / Good") read
/// from <c>config_json.points</c> and <c>config_json.labels</c>.</para>
/// <para><see cref="Nps"/> is a fixed 0–10 scale with Detractor/Passive/
/// Promoter semantics.</para>
/// <para><see cref="SingleChoice"/> and <see cref="MultiChoice"/> render
/// admin-defined options from <c>config_json.options</c>.</para>
public enum SurveyQuestionType
{
    Rating,
    Nps,
    Text,
    SingleChoice,
    MultiChoice,
}

/// Where a question lives in the survey. Kept in sync with
/// `chk_survey_question_applies_to`.
///
/// <para><see cref="Survey"/> questions are asked exactly once per response.</para>
/// <para><see cref="Agent"/> questions render once per contributing agent on
/// the ticket — the customer answers the full sub-question set for each
/// attributed agent. Replaces the deprecated tri-state
/// <c>SurveyAgentRatingMode</c>.</para>
public enum SurveyQuestionScope
{
    Survey,
    Agent,
}

/// Invitation lifecycle. Only Sent rows are reachable via the public token
/// endpoint; all others 404 to avoid leaking whether a token ever existed.
/// Kept in sync with `chk_survey_invitation_status`.
public enum SurveyInvitationStatus
{
    Sent,
    Submitted,
    Expired,
    Cancelled,
}
