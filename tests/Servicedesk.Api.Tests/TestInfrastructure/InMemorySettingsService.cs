using System.Collections.Concurrent;
using System.Globalization;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// The one settings fake for every test: pre-loads every value from
/// <see cref="SettingDefaults.All"/>, so tests run against the same defaults
/// the app ships with, and overrides only what a test is about. An unknown key
/// throws, as an unregistered key does at runtime.
public sealed class InMemorySettingsService : ISettingsService
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public InMemorySettingsService() => Reset();

    public void Set(string key, string value) => _values[key] = value;

    /// Fluent override for unit tests that build the service inline:
    /// <c>new InMemorySettingsService().With(SettingKeys.Mail.MoveOnIngest, false)</c>.
    public InMemorySettingsService With(string key, object value)
    {
        _values[key] = Format(value);
        return this;
    }

    /// Back to the shipped defaults (SharedHostFixture resets between tests).
    public void Reset()
    {
        _values.Clear();
        foreach (var d in SettingDefaults.All)
        {
            _values[d.Key] = d.Value;
        }
    }

    public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!_values.TryGetValue(key, out var raw))
        {
            throw new KeyNotFoundException($"Unknown setting '{key}' in InMemorySettingsService.");
        }
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(string))
        {
            return Task.FromResult((T)(object)raw);
        }
        if (target == typeof(bool))
        {
            return Task.FromResult((T)(object)bool.Parse(raw));
        }
        return Task.FromResult((T)Convert.ChangeType(raw, target, CultureInfo.InvariantCulture));
    }

    public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default)
    {
        _values[key] = value is null ? "" : Format(value);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

/// In-memory <see cref="IProtectedSecretStore"/> so secret-gated endpoints
/// can be exercised without the Npgsql-backed store. Set values via
/// <see cref="Set"/> before issuing the request.
public sealed class InMemorySecretStore : IProtectedSecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public void Set(string key, string value) => _values[key] = value;

    public void Clear() => _values.Clear();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string plaintext, CancellationToken ct = default)
    {
        _values[key] = plaintext;
        return Task.CompletedTask;
    }

    public Task<bool> HasAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.ContainsKey(key));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
