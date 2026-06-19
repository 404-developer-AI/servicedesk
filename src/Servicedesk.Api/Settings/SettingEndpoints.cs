using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Settings;

public static class SettingEndpoints
{
    public static IEndpointRouteBuilder MapSettingEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Admin CRUD ----
        var admin = app.MapGroup("/api/settings")
            .WithTags("Settings")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/", async (ISettingsService svc, [FromQuery] string? category, CancellationToken ct) =>
        {
            var entries = await svc.ListAsync(category, ct);
            return Results.Ok(entries);
        }).WithName("ListSettings").WithOpenApi();

        admin.MapPut("/{key}", async (
            string key,
            [FromBody] UpdateSettingRequest req,
            HttpContext http,
            ISettingsService svc,
            CancellationToken ct) =>
        {
            var actor = http.User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown";
            var actorRole = http.User.FindFirst(ClaimTypes.Role)?.Value ?? "unknown";
            try
            {
                await svc.SetAsync<string>(key, req.Value, actor, actorRole, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Unknown setting key '{key}'." });
            }
        }).WithName("UpdateSetting").WithOpenApi();

        // ---- Navigation settings (any authenticated agent/admin) ----
        app.MapGet("/api/settings/navigation", async (ISettingsService svc, CancellationToken ct) =>
        {
            var showOpenTickets = await svc.GetAsync<bool>(SettingKeys.Navigation.ShowOpenTickets, ct);
            return Results.Ok(new NavigationSettings(showOpenTickets));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetNavigationSettings")
        .WithOpenApi();

        // ---- Notification settings (v0.0.12 stap 4, agent-readable) ----
        // The notification raamwerk needs the toast duration on the client.
        // Exposing the two public-safe knobs here keeps the settings read
        // off the admin-only `/api/settings` endpoint.
        app.MapGet("/api/settings/notifications", async (ISettingsService svc, CancellationToken ct) =>
        {
            var popupDuration = await svc.GetAsync<int>(SettingKeys.Notifications.PopupDurationSeconds, ct);
            if (popupDuration <= 0) popupDuration = 10;
            return Results.Ok(new NotificationsSettings(popupDuration));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetNotificationsSettings")
        .WithOpenApi();

        // ---- Mail compose settings (v0.0.85, agent-readable) ----
        // The composer's "forgotten attachment" warning needs the enable flag
        // and keyword list on the client. Exposed here (agent-safe projection)
        // so the read stays off the admin-only `/api/settings` endpoint.
        app.MapGet("/api/settings/mail-compose", async (ISettingsService svc, CancellationToken ct) =>
        {
            var enabled = await svc.GetAsync<bool>(SettingKeys.Mail.ForgottenAttachmentEnabled, ct);
            var keywords = await svc.GetAsync<string>(SettingKeys.Mail.ForgottenAttachmentKeywords, ct) ?? string.Empty;
            return Results.Ok(new MailComposeSettings(enabled, keywords));
        })
        .WithTags("Settings")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithName("GetMailComposeSettings")
        .WithOpenApi();

        return app;
    }

    public sealed record UpdateSettingRequest([property: Required] string Value);
    public sealed record NavigationSettings(bool ShowOpenTickets);
    public sealed record NotificationsSettings(int PopupDurationSeconds);
    public sealed record MailComposeSettings(bool ForgottenAttachmentEnabled, string ForgottenAttachmentKeywords);
}
