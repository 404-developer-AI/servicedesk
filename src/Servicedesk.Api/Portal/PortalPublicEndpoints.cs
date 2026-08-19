using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Portal;

/// Anonymous, read-only projection of the portal settings the public pages
/// need before sign-in (same pattern as /api/system/maintenance): only the
/// safe, derived fields — never the raw settings list.
public static class PortalPublicEndpoints
{
    public static IEndpointRouteBuilder MapPortalPublicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/portal/config", async (ISettingsService settings, CancellationToken ct) =>
        {
            bool enabled = false, registrationEnabled = false, turnstileEnabled = false, newTicketEnabled = false;
            string siteKey = string.Empty, action = string.Empty, organisation = string.Empty, intro = string.Empty;
            var minPassword = 12;
            try
            {
                enabled = await settings.GetAsync<bool>(SettingKeys.Portal.Enabled, ct);
                if (enabled)
                {
                    registrationEnabled = await settings.GetAsync<bool>(SettingKeys.Portal.RegistrationEnabled, ct);
                    turnstileEnabled = await settings.GetAsync<bool>(SettingKeys.Portal.TurnstileEnabled, ct);
                    siteKey = await settings.GetAsync<string>(SettingKeys.Portal.TurnstileSiteKey, ct) ?? string.Empty;
                    action = await settings.GetAsync<string>(SettingKeys.Portal.TurnstileAction, ct) ?? string.Empty;
                    organisation = await settings.GetAsync<string>(SettingKeys.Portal.OrganisationName, ct) ?? string.Empty;
                    intro = await settings.GetAsync<string>(SettingKeys.Portal.RegistrationIntroHtml, ct) ?? string.Empty;
                    minPassword = await settings.GetAsync<int>(SettingKeys.Portal.PasswordMinimumLength, ct);
                    newTicketEnabled = Guid.TryParse(await settings.GetAsync<string>(SettingKeys.Portal.NewTicketQueueId, ct), out _);
                }
            }
            catch
            {
                // Settings store unreachable — report disabled; the page shows the unavailable notice.
                enabled = false;
            }

            if (!enabled) return Results.Ok(new { enabled = false });
            return Results.Ok(new
            {
                enabled = true,
                registrationEnabled,
                newTicketEnabled,
                organisationName = string.IsNullOrWhiteSpace(organisation) ? "Servicedesk" : organisation.Trim(),
                registrationIntroHtml = intro,
                passwordMinimumLength = Math.Max(8, minPassword),
                turnstile = turnstileEnabled && !string.IsNullOrWhiteSpace(siteKey)
                    ? new { enabled = true, siteKey = siteKey.Trim(), action }
                    : new { enabled = turnstileEnabled, siteKey = string.Empty, action },
            });
        })
        .WithName("PortalPublicConfig")
        .WithTags("PortalPublic")
        .WithOpenApi();
        return app;
    }
}
