using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Phones;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// Shared helpers for the real-SQL search source tests: unique tokens (the
/// test database is shared across tests), principals and zero-hit assertions.
internal static class SearchTestKit
{
    public static string Token(string prefix = "kiwi") => $"{prefix}{Guid.NewGuid():N}"[..13];

    public static SearchRequest Query(string q) => new(q, Type: null, Limit: 50, Offset: 0);

    public static SearchPrincipal Admin() => new(Guid.NewGuid(), "Admin", allowedQueueIds: null);

    public static SearchPrincipal Agent(params string[] features) =>
        new(Guid.NewGuid(), "Agent", Array.Empty<Guid>(), new HashSet<string>(features));

    public static SearchPrincipal AgentWithId(Guid userId, params string[] features) =>
        new(userId, "Agent", Array.Empty<Guid>(), new HashSet<string>(features));

    /// A customer, even one carrying every feature flag, never gets search hits.
    public static SearchPrincipal Customer(params string[] features) =>
        new(Guid.NewGuid(), "Customer", Array.Empty<Guid>(), new HashSet<string>(features));

    public static string[] Ids(SearchGroup group) => group.Hits.Select(h => h.EntityId).Order().ToArray();

    public static string[] Sorted(params object[] ids) => ids.Select(i => i.ToString()!).Order().ToArray();

    public static async Task AssertNoHitsAsync(ISearchSource source, SearchPrincipal principal, string token)
    {
        Assert.False(source.IsAvailableFor(principal));
        Assert.Empty((await source.SearchAsync(Query(token), principal, default)).Hits);
    }
}

/// Settings stand-in that returns one fixed string for every key read.
internal sealed class FixedStringSettings : ISettingsService
{
    private readonly string _value;

    public FixedStringSettings(string value) => _value = value;

    public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult((T)(object)_value);

    public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class NoPhoneNormalizer : IContactPhoneNormalizer
{
    public Task<string> NormalizeAsync(string? raw, CancellationToken ct = default) => Task.FromResult(string.Empty);

    public Task<(string PhoneE164, string MobilePhoneE164)> NormalizePairAsync(
        string? phone, string? mobilePhone, CancellationToken ct = default) =>
        Task.FromResult((string.Empty, string.Empty));
}
