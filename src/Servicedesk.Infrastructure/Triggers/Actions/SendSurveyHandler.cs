using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Surveys;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Trigger action that hands off to <see cref="ISurveyDispatchService"/>.
/// Idempotent: the dispatch service skips when an active invitation already
/// exists for this (survey, ticket) pair, so a chatty rule that re-matches
/// many times still only sends one mail.
///
/// <para>Action JSON:</para>
/// <code>
/// {
///   "kind": "send_survey",
///   "survey_id": "guid",
///   "ttl_days_override": 7,        // optional
///   "recipient_override": "x@y.com" // optional
/// }
/// </code>
internal sealed class SendSurveyHandler : ITriggerActionHandler
{
    private readonly ISurveyDispatchService _dispatch;
    private readonly ILogger<SendSurveyHandler> _logger;

    public SendSurveyHandler(ISurveyDispatchService dispatch, ILogger<SendSurveyHandler> logger)
    {
        _dispatch = dispatch;
        _logger = logger;
    }

    public string Kind => "send_survey";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "survey_id", out var surveyId))
            return TriggerActionResult.Failed(Kind, "Action requires 'survey_id'.");

        int? ttlOverride = null;
        if (actionJson.TryGetProperty("ttl_days_override", out var ttlProp) && ttlProp.ValueKind == JsonValueKind.Number)
        {
            if (ttlProp.TryGetInt32(out var ttl) && ttl > 0 && ttl < 3650) ttlOverride = ttl;
        }

        string? recipientOverride = null;
        if (ActionJson.TryReadString(actionJson, "recipient_override", out var rec))
            recipientOverride = rec;

        var outcome = await _dispatch.DispatchAsync(new SurveyDispatchRequest(
            SurveyId: surveyId,
            TicketId: ctx.TicketId,
            TtlDaysOverride: ttlOverride,
            RecipientOverride: recipientOverride,
            ActorUserId: null), ct);

        switch (outcome.Status)
        {
            case SurveyDispatchStatus.Sent:
                return TriggerActionResult.Applied(Kind, new
                {
                    invitationId = outcome.InvitationId,
                    sentEventId = outcome.SentEventId,
                });
            case SurveyDispatchStatus.Skipped:
                return TriggerActionResult.NoOp(Kind, new { reason = outcome.Reason });
            case SurveyDispatchStatus.Failed:
            default:
                _logger.LogWarning(
                    "SendSurveyHandler failed for ticket {TicketId} survey {SurveyId}: {Reason}",
                    ctx.TicketId, surveyId, outcome.Reason);
                return TriggerActionResult.Failed(Kind, outcome.Reason ?? "Unknown failure.");
        }
    }
}
