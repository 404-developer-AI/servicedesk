using System.Globalization;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Health;

/// Formats UTC timestamps in health summaries/details in the configured
/// display zone (<see cref="SettingKeys.App.TimeZone"/>), matching the
/// "dd/MM/yyyy - HH:mm" style used across the UI.
internal static class HealthTimeFormat
{
    private const string Pattern = "dd/MM/yyyy - HH:mm";

    public static async Task<TimeZoneInfo> ResolveDisplayZoneAsync(ISettingsService settings, CancellationToken ct)
    {
        try
        {
            var id = await settings.GetAsync<string>(SettingKeys.App.TimeZone, ct);
            return ResolveTimeZone(id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// Same defensive resolver as <see cref="Integrations.Telavox.TelavoxPollingWorker"/>
    /// — keep the two in sync so the health tile and the worker agree about
    /// which timezone is in play.
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* Invalid IANA id — fall through. */ }
        }
        return TimeZoneInfo.Local;
    }

    public static string Local(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            .ToString(Pattern, CultureInfo.InvariantCulture);

    public static string Local(DateTimeOffset utc, TimeZoneInfo tz) => Local(utc.UtcDateTime, tz);
}
