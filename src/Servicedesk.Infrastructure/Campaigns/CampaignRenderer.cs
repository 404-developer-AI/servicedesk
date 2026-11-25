using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Time;

namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — campaign-only tokens on top of the compose tokens.
public static class CampaignTokens
{
    public const string Name = "{{campaign.name}}";
    public const string ProposedDate = "{{campaign.proposedDate}}";
    public const string ProposedTime = "{{campaign.proposedTime}}";
    public const string ProposedDateTime = "{{campaign.proposedDateTime}}";
    /// Phase 2: the customer's answer link (approve / other moment / decline).
    public const string ApprovalLink = "{{campaign.approvalLink}}";

    public static readonly IReadOnlyList<(string Token, string Label)> Supported = new[]
    {
        (ProposedDate, "Campaign · Proposed date"),
        (ProposedTime, "Campaign · Proposed time"),
        (ProposedDateTime, "Campaign · Proposed date and time"),
        (ApprovalLink, "Campaign · Answer link (approve / other moment / decline)"),
        (Name, "Campaign · Name"),
    };
}

/// How proposed dates are written: the server zone (App.TimeZone) plus the
/// Campaigns.Proposed* formats. Read once per batch.
public sealed record CampaignFormat(TimeZoneInfo Zone, string DateFormat, string TimeFormat, CultureInfo Culture)
{
    public static async Task<CampaignFormat> LoadAsync(ISettingsService settings, CancellationToken ct)
    {
        var zoneId = await settings.GetOrAsync<string?>(SettingKeys.App.TimeZone, null, ct);
        var dateFormat = await settings.GetOrAsync<string?>(SettingKeys.Campaigns.ProposedDateFormat, null, ct);
        var timeFormat = await settings.GetOrAsync<string?>(SettingKeys.Campaigns.ProposedTimeFormat, null, ct);
        var cultureName = await settings.GetOrAsync<string?>(SettingKeys.Campaigns.ProposedDateCulture, null, ct);
        return Create(zoneId, dateFormat, timeFormat, cultureName);
    }

    public static CampaignFormat Create(string? zoneId, string? dateFormat, string? timeFormat, string? cultureName)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(cultureName) ? "nl-BE" : cultureName.Trim()); }
        catch (CultureNotFoundException) { culture = CultureInfo.GetCultureInfo("nl-BE"); }
        return new CampaignFormat(
            TimeZoneResolver.Resolve(zoneId, TimeZoneInfo.Local),
            string.IsNullOrWhiteSpace(dateFormat) ? "dddd d MMMM yyyy" : dateFormat,
            string.IsNullOrWhiteSpace(timeFormat) ? "HH:mm" : timeFormat,
            culture);
    }
}

public sealed record CampaignRenderResult(string Subject, string BodyHtml, IReadOnlyList<string> MissingTokens);

/// Renders a campaign message for one ticket: the ticket's compose-token map
/// (contact, company, ticket) plus the campaign tokens. Pure — values are
/// HTML-encoded in the body and single-line in the subject
/// (<see cref="ComposeTokenRenderer"/>); a token without a value stays in
/// the output and is reported, so the caller can refuse to send.
public static partial class CampaignRenderer
{
    [GeneratedRegex(@"\{\{\s*campaign\.approvalLink\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ApprovalLinkToken();

    /// True when the message uses the answer link (the send step then mints one).
    public static bool UsesApprovalLink(string subject, string bodyHtml) =>
        ApprovalLinkToken().IsMatch(subject) || ApprovalLinkToken().IsMatch(bodyHtml);

    /// The proposed moment as the customer reads it ("date time"), or null.
    public static string? ProposedText(DateTime? proposedUtc, CampaignFormat format)
    {
        if (proposedUtc is not { } utc) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), format.Zone);
        return $"{SafeFormat(local, format.DateFormat, format.Culture)} {SafeFormat(local, format.TimeFormat, format.Culture)}";
    }

    /// The customer's preferred moment (a server-local date + optional time of
    /// day) in the campaign's date/time format: "woensdag 16 december 2026 14:00".
    public static string PreferredText(DateTime date, int? minute, CampaignFormat format)
    {
        var day = SafeFormat(date.Date, format.DateFormat, format.Culture);
        return minute is { } m ? $"{day} {SafeFormat(date.Date.AddMinutes(m), format.TimeFormat, format.Culture)}" : day;
    }

    /// <paramref name="approvalUrl"/> = the recipient's answer-link URL, or
    /// null when none is available (the token then stays and is reported
    /// missing, so the ticket is skipped instead of mailed without its link).
    /// In the body the link becomes an anchor with <paramref name="approvalLabel"/>
    /// (both encoded); in the subject it is the bare URL.
    public static CampaignRenderResult Render(
        string subject, string bodyHtml, string campaignName, DateTime? proposedUtc,
        IReadOnlyDictionary<string, string> ticketTokens, CampaignFormat format,
        string? approvalUrl = null, string approvalLabel = "Answer online")
    {
        var map = new Dictionary<string, string>(ticketTokens, StringComparer.Ordinal)
        {
            [CampaignTokens.Name] = campaignName,
            [CampaignTokens.ApprovalLink] = approvalUrl ?? string.Empty,
        };
        if (!string.IsNullOrEmpty(approvalUrl))
        {
            var anchor = $"<a href=\"{WebUtility.HtmlEncode(approvalUrl)}\">{WebUtility.HtmlEncode(approvalLabel)}</a>";
            bodyHtml = ApprovalLinkToken().Replace(bodyHtml, _ => anchor);
        }
        if (proposedUtc is { } utc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), format.Zone);
            var date = SafeFormat(local, format.DateFormat, format.Culture);
            var time = SafeFormat(local, format.TimeFormat, format.Culture);
            map[CampaignTokens.ProposedDate] = date;
            map[CampaignTokens.ProposedTime] = time;
            map[CampaignTokens.ProposedDateTime] = $"{date} {time}";
        }
        else
        {
            map[CampaignTokens.ProposedDate] = string.Empty;
            map[CampaignTokens.ProposedTime] = string.Empty;
            map[CampaignTokens.ProposedDateTime] = string.Empty;
        }

        var s = ComposeTokenRenderer.Render(subject, map, ComposeTokenEscape.PlainText);
        var b = ComposeTokenRenderer.Render(bodyHtml, map, ComposeTokenEscape.Html);
        var missing = s.MissingTokens.Concat(b.MissingTokens).Distinct(StringComparer.Ordinal).ToList();
        return new CampaignRenderResult(s.Output.Trim(), b.Output, missing);
    }

    private static string SafeFormat(DateTime local, string format, CultureInfo culture)
    {
        try { return local.ToString(format, culture); }
        catch (FormatException) { return local.ToString("g", culture); }
    }
}
