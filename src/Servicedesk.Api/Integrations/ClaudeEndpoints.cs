using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Claude;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Claude AI ticket-assist integration. Admin-gated; all
/// routes live under /api/admin/integrations/claude-ai. The model, max-tokens,
/// effort, prices, prompt, ZDR confirmation and other tunables are edited via
/// the generic settings surface (category "Claude AI"); this file owns the
/// pieces that need their own shape: the API-key secret, the per-agent monthly
/// budgets, and the usage overview.
public static class ClaudeEndpoints
{
    /// Defensive cap on a per-agent monthly budget: €10,000 in cents. A paste
    /// mistake can't assign a runaway budget.
    private const int MaxBudgetCents = 1_000_000;

    public static IEndpointRouteBuilder MapClaudeEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/claude-ai")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", GetStatus).WithName("GetClaudeStatus").WithOpenApi();

        admin.MapGet("/secret", GetSecretStatus).WithName("GetClaudeSecretStatus").WithOpenApi();
        admin.MapPut("/secret", SetSecret).WithName("SetClaudeSecret").WithOpenApi();
        admin.MapDelete("/secret", DeleteSecret).WithName("DeleteClaudeSecret").WithOpenApi();

        admin.MapGet("/usage", GetUsage).WithName("GetClaudeUsage").WithOpenApi();
        admin.MapPut("/agents/{userId:guid}/budget", SetAgentBudget)
            .WithName("SetClaudeAgentBudget").WithOpenApi();

        return app;
    }

    // ---- /status --------------------------------------------------------

    private static async Task<IResult> GetStatus(
        ISettingsService settings, IProtectedSecretStore secrets, CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Claude.Enabled, ct);
        var hasKey = await secrets.HasAsync(ProtectedSecretKeys.ClaudeApiKey, ct);
        var zdr = await settings.GetAsync<bool>(SettingKeys.Claude.ZeroDataRetentionConfirmed, ct);
        var model = await settings.GetAsync<string>(SettingKeys.Claude.Model, ct) ?? "";

        var state = !enabled ? "Disabled"
            : !hasKey ? "NotConfigured"
            : !zdr ? "NeedsZeroDataRetention"
            : "Ready";

        return Results.Ok(new
        {
            state,
            enabled,
            apiKeyConfigured = hasKey,
            zeroDataRetentionConfirmed = zdr,
            model,
        });
    }

    // ---- /secret CRUD ---------------------------------------------------

    private static async Task<IResult> GetSecretStatus(
        IProtectedSecretStore secrets, CancellationToken ct) =>
        Results.Ok(new { configured = await secrets.HasAsync(ProtectedSecretKeys.ClaudeApiKey, ct) });

    public sealed record SetSecretRequest([property: Required] string Value);

    private static async Task<IResult> SetSecret(
        [FromBody] SetSecretRequest req,
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Value))
            return Results.BadRequest(new { error = "missing_value", message = "API key is required." });
        // Anthropic keys are short opaque tokens; a multi-KB paste is user error.
        var trimmed = req.Value.Trim();
        if (trimmed.Length > 512)
            return Results.BadRequest(new { error = "invalid_value", message = "API key exceeds 512 characters." });

        await secrets.SetAsync(ProtectedSecretKeys.ClaudeApiKey, trimmed, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ClaudeEventTypes.ApiKeyUpdated,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.ClaudeApiKey,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = true }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSecret(
        HttpContext http,
        IProtectedSecretStore secrets,
        IAuditLogger audit,
        CancellationToken ct)
    {
        await secrets.DeleteAsync(ProtectedSecretKeys.ClaudeApiKey, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ClaudeEventTypes.ApiKeyDeleted,
            Actor: actor,
            ActorRole: role,
            Target: ProtectedSecretKeys.ClaudeApiKey,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { configured = false }), ct);
        return Results.NoContent();
    }

    // ---- /usage ---------------------------------------------------------

    private static async Task<IResult> GetUsage(
        ISettingsService settings, IClaudeUsageStore usage, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var defaultBudgetCents = await settings.GetAsync<int>(SettingKeys.Claude.DefaultMonthlyBudgetEurCents, ct);

        var rows = await usage.GetAgentMonthlyUsageAsync(monthStart, ct);
        return Results.Ok(new
        {
            monthStartUtc = monthStart,
            defaultBudgetCents,
            agents = rows.Select(r => new
            {
                userId = r.UserId,
                email = r.Email,
                role = r.RoleName,
                budgetOverrideCents = r.BudgetOverrideCents,
                effectiveBudgetCents = r.BudgetOverrideCents ?? defaultBudgetCents,
                monthSpendMicroEur = r.MonthSpendMicroEur,
                callCount = r.CallCount,
            }),
        });
    }

    // ---- /agents/{id}/budget --------------------------------------------

    public sealed record BudgetRequest(int? Cents);

    private static async Task<IResult> SetAgentBudget(
        Guid userId,
        [FromBody] BudgetRequest req,
        HttpContext http,
        IClaudeUsageStore usage,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (req is null)
            return Results.BadRequest(new { error = "missing_body" });
        if (req.Cents is < 0 or > MaxBudgetCents)
            return Results.BadRequest(new
            {
                error = "invalid_budget",
                message = $"Budget must be between 0 and {MaxBudgetCents} cents, or null to use the default.",
            });

        await usage.SetUserBudgetOverrideCentsAsync(userId, req.Cents, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ClaudeEventTypes.AgentBudgetUpdated,
            Actor: actor,
            ActorRole: role,
            Target: userId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { budgetCents = req.Cents }), ct);
        return Results.Ok(new { userId, budgetOverrideCents = req.Cents });
    }
}
