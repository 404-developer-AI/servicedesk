using System.Text.Json;
using Servicedesk.Infrastructure.Surveys;

namespace Servicedesk.Infrastructure.Triggers.Actions.Previewers;

/// Dry-run for <c>send_survey</c>. Resolves the survey name + active flag
/// without minting a token or sending mail; reports an existing active
/// invitation as <c>WouldNoOp</c> so the admin sees the idempotency at a
/// glance in the trigger-test pane.
internal sealed class SendSurveyPreviewer : ITriggerActionPreviewer
{
    private readonly ISurveyRepository _surveys;
    private readonly ISurveyInvitationRepository _invitations;

    public SendSurveyPreviewer(ISurveyRepository surveys, ISurveyInvitationRepository invitations)
    {
        _surveys = surveys;
        _invitations = invitations;
    }

    public string Kind => "send_survey";

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "survey_id", out var surveyId))
            return TriggerActionPreviewResult.Failed(Kind, "Action requires 'survey_id'.");

        var survey = await _surveys.GetAsync(surveyId, ct);
        if (survey is null)
            return TriggerActionPreviewResult.Failed(Kind, "Survey not found.");
        if (!survey.IsActive)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = "Survey is inactive.", surveyName = survey.Name });

        var active = await _invitations.ActiveExistsAsync(surveyId, ctx.TicketId, ct);
        if (active)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = "Active invitation already exists for this ticket.", surveyName = survey.Name });

        // The dry-run pane shows the survey + per-agent question counts so
        // the admin can verify they linked the right survey without the
        // dispatch actually firing.
        var surveyQuestionCount = survey.Questions.Count(q => q.AppliesTo == Servicedesk.Domain.Surveys.SurveyQuestionScope.Survey);
        var agentQuestionCount = survey.Questions.Count(q => q.AppliesTo == Servicedesk.Domain.Surveys.SurveyQuestionScope.Agent);
        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            surveyId = survey.Id,
            surveyName = survey.Name,
            ttlDays = survey.TtlDays,
            surveyQuestionCount,
            agentQuestionCount,
        });
    }
}
