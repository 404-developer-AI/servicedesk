using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Claude;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the knowledge-base chat assistant (v0.0.86). Agent+Admin
/// only. The retrieval scoping, budget enforcement, tool-use loop and usage
/// logging live in <see cref="IKbChatService"/>; this endpoint only authorizes
/// the caller, shapes the request/response and maps the outcome to HTTP.
public static class KbChatEndpoints
{
    /// Defensive cap on transcript length accepted from the client; the service
    /// additionally trims to the configurable rolling window.
    private const int MaxHistoryMessages = 60;

    public static IEndpointRouteBuilder MapKbChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai/kb-chat")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/status", GetStatus).WithName("GetKbChatStatus").WithOpenApi();
        group.MapPost("", SendTurn).WithName("SendKbChatTurn").WithOpenApi();

        return app;
    }

    // ---- /status --------------------------------------------------------

    /// Tells the client whether to show the floating chat button. "ready" means
    /// the feature is on AND the shared API key + zero-data-retention gate are
    /// satisfied; the client additionally checks the agent's own KB access.
    private static async Task<IResult> GetStatus(
        ISettingsService settings, IProtectedSecretStore secrets, CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Claude.KbChatEnabled, ct);
        var hasKey = await secrets.HasAsync(ProtectedSecretKeys.ClaudeApiKey, ct);
        var zdr = await settings.GetAsync<bool>(SettingKeys.Claude.ZeroDataRetentionConfirmed, ct);
        return Results.Ok(new { enabled, ready = enabled && hasKey && zdr });
    }

    // ---- POST /api/ai/kb-chat ------------------------------------------

    public sealed record TurnMessage(string? Role, string? Text);
    public sealed record TurnRequest(IReadOnlyList<TurnMessage>? History, string? Message);

    private static async Task<IResult> SendTurn(
        [FromBody] TurnRequest? req,
        HttpContext http,
        IKbChatService chat,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Message))
            return Results.BadRequest(new { error = "missing_message", message = "A message is required." });

        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        var history = (req.History ?? Array.Empty<TurnMessage>())
            .TakeLast(MaxHistoryMessages)
            .Select(m => new KbChatMessage(
                (m.Role ?? string.Empty).Trim().ToLowerInvariant(),
                m.Text ?? string.Empty))
            .ToList();

        KbChatResult result;
        try
        {
            result = await chat.SendAsync(userId, history, req.Message!, ct);
        }
        catch (ClaudeApiException ex)
        {
            await WriteAuditAsync(audit, http, "error", ct);
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await WriteAuditAsync(audit, http, result.Outcome.ToString(), ct);

        if (result.Outcome == KbChatOutcome.Ok)
        {
            return Results.Ok(new
            {
                reply = result.ReplyText,
                replyHtml = result.ReplyHtml,
                citations = result.Citations.Select(c => new
                {
                    articleId = c.ArticleId,
                    title = c.Title,
                    slug = c.Slug,
                    sectionId = c.SectionId,
                }),
                monthSpendMicroEur = result.MonthSpendMicroEur,
                monthBudgetMicroEur = result.MonthBudgetMicroEur,
            });
        }

        var error = result.Outcome switch
        {
            KbChatOutcome.Disabled => "disabled",
            KbChatOutcome.NotConfigured => "not_configured",
            KbChatOutcome.ZdrNotConfirmed => "zdr_not_confirmed",
            KbChatOutcome.NoKbAccess => "no_kb_access",
            KbChatOutcome.NoBudget => "no_budget",
            KbChatOutcome.BudgetExceeded => "budget_exceeded",
            _ => "unknown",
        };
        return Results.Json(new
        {
            error,
            message = result.Message,
            monthSpendMicroEur = result.MonthSpendMicroEur,
            monthBudgetMicroEur = result.MonthBudgetMicroEur,
        }, statusCode: 409);
    }

    private static async Task WriteAuditAsync(
        IAuditLogger audit, HttpContext http, string outcome, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ClaudeEventTypes.KbChatRequested,
            Actor: actor,
            ActorRole: role,
            Target: "kb-chat",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { outcome }), ct);
    }
}
