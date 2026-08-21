using Dapper;
using Npgsql;

namespace Servicedesk.Api.Security;

/// v0.1.2 (audit v0.1.1 #10) — the rate-limit budgets shown in Settings →
/// Security / Portal / IntakeForms / Surveys / KnowledgeBase are now actually
/// read: once, at startup, straight from the settings table (the settings
/// cache and its DI graph don't exist yet at AddRateLimiter time). Resolution
/// order per key: explicit environment configuration
/// (SERVICEDESK_Security__RateLimit__…) &gt; settings DB row &gt; code default —
/// env stays on top so installs tuned before v0.1.2 keep their behavior. A
/// change in the Settings UI requires an app restart, which every affected
/// setting description states.
///
/// On a fresh install the settings table does not exist yet when this runs
/// (the bootstrapper is a hosted service); the loader then returns an empty
/// map and every budget uses its code default — which equals the seeded DB
/// default, so behavior converges after the first restart regardless.
internal sealed class StartupRateLimitSettings
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private StartupRateLimitSettings(IReadOnlyDictionary<string, string> values) => _values = values;

    public static StartupRateLimitSettings Load(IConfiguration configuration, Serilog.ILogger logger)
    {
        try
        {
            var connectionString = configuration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new StartupRateLimitSettings(new Dictionary<string, string>(StringComparer.Ordinal));
            }

            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            var rows = connection.Query<(string Key, string Value)>(
                new CommandDefinition(
                    "SELECT key AS Key, value AS Value FROM settings WHERE key LIKE '%RateLimit%'",
                    commandTimeout: 5));
            return new StartupRateLimitSettings(
                rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal));
        }
        catch (Exception ex)
        {
            // Fresh install (no table yet) or Postgres still waking up — the
            // code defaults apply for this process lifetime.
            logger.Information("Rate-limit settings not readable at startup ({Reason}); using configured/code defaults.", ex.GetType().Name);
            return new StartupRateLimitSettings(new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    public int? GetInt(string settingKey)
        => _values.TryGetValue(settingKey, out var raw) && int.TryParse(raw, out var value) && value > 0
            ? value
            : null;
}
