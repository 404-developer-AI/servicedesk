using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Settings;

public static class SettingsServiceExtensions
{
    /// Reads a setting, or returns <paramref name="fallback"/> when the read
    /// fails (an unparsable value, an unregistered key in a test host, a
    /// settings store that is not reachable yet). For the reads that must
    /// never fail their caller — anonymous endpoints, health and background
    /// loops. A cancelled read still throws: cancellation is not a fallback.
    /// With a <paramref name="logger"/> the failure is logged as a warning
    /// (key and exception type only — never the value).
    public static async Task<T> GetOrAsync<T>(
        this ISettingsService settings, string key, T fallback, CancellationToken ct = default,
        ILogger? logger = null)
    {
        try
        {
            return await settings.GetAsync<T>(key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Setting {Key} could not be read ({Error}); using the fallback.", key, ex.GetType().Name);
            return fallback;
        }
    }
}
