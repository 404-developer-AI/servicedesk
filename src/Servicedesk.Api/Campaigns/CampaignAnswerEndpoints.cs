using Microsoft.AspNetCore.Mvc;
using Servicedesk.Infrastructure.Campaigns;

namespace Servicedesk.Api.Campaigns;

/// <see cref="PreferredDate"/> ("yyyy-MM-dd", server-local) + optional
/// <see cref="PreferredMinute"/> (minutes after midnight) with "Another moment".
public sealed record CampaignAnswerRequest(string? Choice, string? Note, DateTime? PreferredDate = null, int? PreferredMinute = null);

/// v0.1.67 (phase 2) — the public side of the campaign answer link
/// (<c>/campaign-answer/{token}</c> in the SPA). No session: the 256-bit
/// token is the only credential (looked up by its SHA-256 hash). CSRF-exempt
/// like the intake and survey links (DoubleSubmitCsrfMiddleware), rate-limited
/// per IP by the shared "intake-public" policy for public token pages. The
/// response is a whitelist — campaign name, ticket reference, proposed
/// moment and the customer's own answer — never internal ticket data.
/// Unknown and malformed tokens are both a plain 404.
public static class CampaignAnswerEndpoints
{
    public static IEndpointRouteBuilder MapCampaignAnswerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/campaign-answers")
            .WithTags("CampaignAnswersPublic")
            .RequireRateLimiting("intake-public");

        group.MapGet("/{token}", async (string token, ICampaignAnswerService answers, CancellationToken ct) =>
            Respond(await answers.GetAsync(token, ct))).WithName("GetCampaignAnswer");

        group.MapPost("/{token}", async (string token, [FromBody] CampaignAnswerRequest req, HttpContext http,
            ICampaignAnswerService answers, CancellationToken ct) =>
            Respond(await answers.SubmitAsync(token, req.Choice, req.Note, req.PreferredDate, req.PreferredMinute,
                http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), ct)))
            .WithName("SubmitCampaignAnswer");

        return app;
    }

    private static IResult Respond(CampaignAnswerResult r) => r.Outcome switch
    {
        CampaignAnswerOutcome.NotFound => Results.NotFound(),
        CampaignAnswerOutcome.Expired => Results.Json(new { status = "expired" }, statusCode: StatusCodes.Status410Gone),
        CampaignAnswerOutcome.Invalid => Results.BadRequest(new { error = r.Error }),
        _ => Results.Ok(new
        {
            status = "open",
            campaignName = r.View!.CampaignName,
            ticketNumber = r.View.TicketNumber,
            proposedText = r.View.ProposedText,
            expiresUtc = r.View.ExpiresUtc,
            expiresText = r.View.ExpiresText,
            answer = r.View.Answer,
            note = r.View.Note,
            answeredUtc = r.View.AnsweredUtc,
            answeredText = r.View.AnsweredText,
            otherTimeNotice = r.View.OtherTimeNotice,
            preferredMinDate = r.View.PreferredMinDate,
            preferredMaxDate = r.View.PreferredMaxDate,
            preferredDate = r.View.PreferredDate,
            preferredMinute = r.View.PreferredMinute,
            preferredText = r.View.PreferredText,
        }),
    };
}
