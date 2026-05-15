using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.ComposeTemplates;

namespace Servicedesk.Infrastructure.Surveys;

/// Resolves a list of compose-template ids → linked survey ids and fans
/// each one out to <see cref="ISurveyDispatchService"/>. Used by the
/// outbound-mail + add-event endpoints so an agent who applies a compose
/// template with a linked survey automatically dispatches the survey on
/// successful send. Idempotent: the underlying dispatch skips on active-pair
/// collisions, so repeating the same templates on the same ticket only sends
/// one invitation.
public interface IComposeTemplateSurveyDispatcher
{
    Task DispatchForTemplatesAsync(
        Guid ticketId,
        IReadOnlyList<Guid> composeTemplateIds,
        Guid? actorUserId,
        CancellationToken ct);
}

public sealed class ComposeTemplateSurveyDispatcher : IComposeTemplateSurveyDispatcher
{
    private readonly IComposeTemplateRepository _templates;
    private readonly ISurveyDispatchService _dispatch;
    private readonly ILogger<ComposeTemplateSurveyDispatcher> _logger;

    public ComposeTemplateSurveyDispatcher(
        IComposeTemplateRepository templates,
        ISurveyDispatchService dispatch,
        ILogger<ComposeTemplateSurveyDispatcher> logger)
    {
        _templates = templates;
        _dispatch = dispatch;
        _logger = logger;
    }

    public async Task DispatchForTemplatesAsync(
        Guid ticketId,
        IReadOnlyList<Guid> composeTemplateIds,
        Guid? actorUserId,
        CancellationToken ct)
    {
        if (composeTemplateIds.Count == 0) return;

        // De-duplicate at the survey level: an agent who applies two
        // different templates that both link the same survey still gets
        // one dispatch. (The dispatch service would skip the second one
        // anyway, but de-duping early avoids the log line.)
        var seenSurveys = new HashSet<Guid>();
        foreach (var templateId in composeTemplateIds.Distinct())
        {
            var template = await _templates.GetAsync(templateId, ct);
            if (template?.LinkedSurveyId is not Guid surveyId) continue;
            if (!seenSurveys.Add(surveyId)) continue;

            try
            {
                var outcome = await _dispatch.DispatchAsync(new SurveyDispatchRequest(
                    SurveyId: surveyId,
                    TicketId: ticketId,
                    TtlDaysOverride: null,
                    RecipientOverride: null,
                    ActorUserId: actorUserId), ct);
                if (outcome.Status == SurveyDispatchStatus.Failed)
                {
                    _logger.LogWarning(
                        "Compose-template-linked survey dispatch failed for ticket {TicketId} survey {SurveyId}: {Reason}",
                        ticketId, surveyId, outcome.Reason);
                }
            }
            catch (Exception ex)
            {
                // Mail-send failure is already swallowed inside the
                // dispatch service. Anything escaping here is a bug we
                // want logged but should never roll back the reply itself.
                _logger.LogError(ex,
                    "Compose-template-linked survey dispatch threw for ticket {TicketId} survey {SurveyId}.",
                    ticketId, surveyId);
            }
        }
    }
}
