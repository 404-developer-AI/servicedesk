namespace Servicedesk.Infrastructure.Settings;

public interface ISettingsService
{
    /// Seeds any missing default rows. Safe to call on every startup.
    Task EnsureDefaultsAsync(CancellationToken cancellationToken = default);

    /// Returns the typed value for <paramref name="key"/>. Falls back to the
    /// seeded default if the row is missing. Throws on type mismatch.
    Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// Writes a new value for <paramref name="key"/>. Audits the change via
    /// <see cref="Servicedesk.Infrastructure.Audit.IAuditLogger"/>.
    Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default);
}
