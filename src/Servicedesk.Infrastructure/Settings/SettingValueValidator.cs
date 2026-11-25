using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Settings;

/// Write-side validation for the settings store (v0.1.3, audit v0.1.1 #9).
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
    public static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

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
            SettingKeys.Insights.DefaultPeriod =>
                value is "today" or "week" or "month" or "year" ? null : "Choose today, week, month or year.",
            SettingKeys.Insights.AgentCompareMax =>
                int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 6
                    ? null : "Choose a whole number from 1 to 6.",
            SettingKeys.Insights.OpenedNoActionMinSeconds =>
                int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s) && s is >= 0 and <= 3600
                    ? null : "Choose a whole number of seconds from 0 to 3600.",
            SettingKeys.Insights.WorkflowMailAfterTemplateMinutes => IntRange(value, 0, 240),
            SettingKeys.Insights.WorkflowLimitPriorityMinutes
                or SettingKeys.Insights.WorkflowLimitCallbackMinutes
                or SettingKeys.Insights.WorkflowLimitWfpMinutes
                or SettingKeys.Insights.WorkflowLimitResearchMinutes => IntRange(value, 1, 600),
            SettingKeys.Insights.WorkflowMaxResearchPerAgent => IntRange(value, 1, 20),
            SettingKeys.Insights.WorkflowWfpStatusIds => GuidCsv(value),
            SettingKeys.Insights.RewindIntervalMinutes =>
                value is "5" or "10" or "15" or "30" or "60" ? null : "Choose 5, 10, 15, 30 or 60 minutes.",
            // Served agent-readable and painted into inline styles.
            SettingKeys.Tickets.CallbackColor or SettingKeys.Tickets.ResearchColor =>
                HexColor.IsMatch(value) ? null : "Enter a hex colour like #22c55e.",
            SettingKeys.Tickets.ViewSearchMinChars =>
                int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mc) && mc is >= 1 and <= 10
                    ? null : "Choose a whole number from 1 to 10.",
            SettingKeys.Tickets.ViewSearchDebounceMs =>
                int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var db) && db is >= 0 and <= 2000
                    ? null : "Choose a whole number of milliseconds from 0 to 2000.",
            // v0.1.24 — performance monitoring.
            SettingKeys.Performance.Level =>
                value is "off" or "basic" ? null : "Choose off or basic (Diagnose is started from the Performance page).",
            SettingKeys.Performance.DiagnoseUntilUtc =>
                value.Length == 0 || Performance.PerfSettingsProvider.ParseUtc(value) is not null ? null : "Enter an ISO-8601 UTC timestamp or leave empty.",
            SettingKeys.Performance.RumSamplePercent =>
                int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rum) && rum is >= 0 and <= 100
                    ? null : "Choose a percentage from 0 to 100.",
            SettingKeys.Performance.Budgets =>
                value.Length > 4000 ? "Keep the budget list under 4000 characters."
                : value.Trim().Length == 0 || Performance.Budget.Parse(value).Count > 0 ? null
                : "Write one budget per line, like 'GET /api/tickets/{id:guid}=300'.",
            SettingKeys.Portal.ConversationOrder =>
                value is Portal.PortalConversationOrder.Oldest or Portal.PortalConversationOrder.Newest
                    ? null : "Choose oldest or newest.",
            SettingKeys.Campaigns.MaxRecipients => IntRange(value, 1, 2000),
            SettingKeys.Campaigns.SendRatePerMinute => IntRange(value, 1, 60),
            SettingKeys.Campaigns.MaxAttempts => IntRange(value, 1, 10),
            SettingKeys.Campaigns.ProposedDateFormat or SettingKeys.Campaigns.ProposedTimeFormat => DateFormat(value),
            SettingKeys.Campaigns.ProposedDateCulture => Culture(value),
            SettingKeys.Campaigns.ApprovalLinkMaxDays or SettingKeys.Campaigns.MaxScheduleDays => IntRange(value, 1, 365),
            SettingKeys.Campaigns.ApprovalLinkLabel =>
                value.Trim().Length is >= 1 and <= 120 ? null : "Enter a link text of 1 to 120 characters.",
            _ => null,
        };
    }

    private static string? IntRange(string value, int min, int max)
        => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
           && n >= min && n <= max
            ? null : $"Choose a whole number from {min} to {max}.";

    /// A .NET custom date/time format that formats without throwing.
    private static string? DateFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return "Enter a date format of at most 64 characters.";
        try
        {
            _ = new DateTime(2026, 1, 1, 9, 30, 0).ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }
        catch (FormatException)
        {
            return "This is not a valid date format.";
        }
    }

    /// A culture code the runtime knows (nl-BE, fr-BE, en-GB…).
    private static string? Culture(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Enter a culture code such as nl-BE.";
        try
        {
            var c = System.Globalization.CultureInfo.GetCultureInfo(value.Trim(), predefinedOnly: true);
            return c.Equals(System.Globalization.CultureInfo.InvariantCulture) ? "Enter a culture code such as nl-BE." : null;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return "Unknown culture code. Use a code such as nl-BE, fr-BE or en-GB.";
        }
    }

    /// Comma-separated GUIDs (empty allowed), at most 100.
    private static string? GuidCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 100) return "Pick at most 100 statuses.";
        return parts.All(p => Guid.TryParse(p, out _)) ? null : "Expected a comma-separated list of status ids.";
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
