using Servicedesk.Domain.Surveys;

namespace Servicedesk.Infrastructure.Surveys;

/// Admin CRUD over <c>surveys</c> + <c>survey_questions</c>. The designer page
/// uses <see cref="ListAsync"/> + <see cref="GetAsync"/>; trigger evaluation
/// and compose-template dispatch resolve a single survey at send time via
/// <see cref="GetAsync"/>.
///
/// <para>Soft-deactivate (via <c>IsActive=false</c> on update) keeps the
/// survey + its historical responses intact while hiding it from new
/// pickers. Hard delete is only allowed when no responses exist (see
/// <see cref="DeleteAsync"/>).</para>
public interface ISurveyRepository
{
    Task<IReadOnlyList<SurveySummary>> ListAsync(bool includeInactive, CancellationToken ct);

    Task<Survey?> GetAsync(Guid id, CancellationToken ct);

    /// Used by the public survey page after the invitation snapshot has been
    /// resolved; falls back to live questions when a legacy invitation has
    /// no snapshot (analogous to the v0.0.19 intake fallback).
    Task<Survey?> GetActiveAsync(Guid id, CancellationToken ct);

    Task<Guid> CreateAsync(SurveyMetadataInput metadata, Guid? createdBy, CancellationToken ct);

    Task UpdateMetadataAsync(Guid id, SurveyMetadataInput metadata, bool isActive, CancellationToken ct);

    /// Full-replace of the survey's question list (both Survey-scope and
    /// Agent-scope questions), executed in a single transaction. Existing
    /// rows are deleted and reinserted with the caller-provided sort order;
    /// <c>survey_answers</c> rows have no live FK so historical responses
    /// keep rendering against their invitation snapshot. Returns the
    /// freshly-assigned question ids in input order.
    Task<IReadOnlyList<long>> ReplaceQuestionsAsync(
        Guid surveyId,
        IReadOnlyList<SurveyQuestionInput> questions,
        CancellationToken ct);

    /// Returns false when the survey has responses (a hard delete would
    /// orphan agent leaderboards). Use <see cref="UpdateMetadataAsync"/>
    /// with <c>isActive=false</c> instead.
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    Task<bool> HasResponsesAsync(Guid id, CancellationToken ct);
}

public sealed record SurveySummary(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    int? TtlDays,
    int QuestionCount,
    int AgentQuestionCount,
    int InvitationCount,
    int ResponseCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// Bag of every editable header/label/body on a survey, passed across the
/// repo boundary so adding new label fields doesn't ripple a long parameter
/// list through callers.
public sealed record SurveyMetadataInput(
    string Name,
    string? Description,
    string IntroHtml,
    string InviteSubject,
    string InviteBodyHtml,
    int? TtlDays,
    string? AgentBlockHeading,
    string SubmitButtonLabel,
    string ThankYouMessage,
    string ExpiredMessage,
    string NotFoundMessage);

public sealed record SurveyQuestionInput(
    int SortOrder,
    SurveyQuestionType Type,
    SurveyQuestionScope AppliesTo,
    string Label,
    string? HelpText,
    bool IsRequired,
    /// JSON-stringified config_json — caller is responsible for shape
    /// (<see cref="SurveyQuestionType.Rating"/>:
    /// <c>{"points":int,"labels":string[]?}</c>; choice types:
    /// <c>{"options":[{"value","label"},...]}</c>). Empty object is fine.
    string ConfigJson);
