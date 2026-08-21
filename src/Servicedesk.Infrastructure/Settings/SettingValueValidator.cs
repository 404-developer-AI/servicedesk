using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Settings;

/// Write-side validation for the settings store (v0.1.2, audit v0.1.1 #9).
/// Two layers: the registered <c>ValueType</c> ("int" / "bool") must parse,
/// and a handful of keys with real format requirements get a dedicated rule —
/// URL keys must be http(s) (never <c>javascript:</c> / <c>data:</c>, which
/// would execute in an agent's browser via <c>window.open</c>), cookie names
/// must be header-safe tokens. Everything is admin-only input, but Admin ≠
/// stored XSS against every agent, and a type-mismatched value is an
/// availability foot-gun at read time.
public static class SettingValueValidator
{
    private static readonly Regex CookieName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    /// Returns an error message, or null when the value is acceptable.
    public static string? Validate(SettingDefault def, string value)
    {
        switch (def.ValueType)
        {
            case "int" when !long.TryParse(value, out _):
                return "Value must be a whole number.";
            case "bool" when !bool.TryParse(value, out _):
                return "Value must be 'true' or 'false'.";
        }

        return def.Key switch
        {
            // Served agent-readable and passed to window.open on the client.
            SettingKeys.Copilot.Url => RequireHttpUrl(value, httpsOnly: true, allowEmpty: false),
            // Drives the OIDC redirect_uri, portal links and post-login
            // redirects. http allowed for LAN/dev installs; empty = unset.
            SettingKeys.App.PublicBaseUrl => RequireHttpUrl(value, httpsOnly: false, allowEmpty: true),
            // Flow into Set-Cookie headers — a crafted name is a
            // cookie-header injection primitive.
            SettingKeys.Security.SessionCookieName or SettingKeys.Security.PortalSessionCookieName =>
                CookieName.IsMatch(value) ? null : "Cookie names may only contain letters, digits, '-' and '_' (max 64 characters).",
            _ => null,
        };
    }

    private static string? RequireHttpUrl(string value, bool httpsOnly, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return allowEmpty ? null : "A URL is required.";
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return "Enter an absolute URL.";
        }
        if (uri.Scheme == Uri.UriSchemeHttps) return null;
        if (!httpsOnly && uri.Scheme == Uri.UriSchemeHttp) return null;
        return httpsOnly
            ? "Only https:// URLs are allowed here."
            : "Only http:// or https:// URLs are allowed here.";
    }
}
