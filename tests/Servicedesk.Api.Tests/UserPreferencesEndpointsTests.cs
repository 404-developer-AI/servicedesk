using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Views;
using Servicedesk.Infrastructure.Persistence.Views;
using Servicedesk.Infrastructure.Preferences;
using Xunit;

namespace Servicedesk.Api.Tests;

/// /api/preferences over the preference store: the per-view column lock
/// (v0.1.18 — a locked view refuses a per-view save and ignores user layouts
/// on read) and that every write lands under the caller's own user id.
public sealed class UserPreferencesEndpointsTests : IClassFixture<SharedHostFixture>
{
    private static readonly Guid LockedView = Guid.Parse("e1000000-0000-0000-0000-000000000001");
    private static readonly Guid OpenView = Guid.Parse("e1000000-0000-0000-0000-000000000002");

    private readonly SharedHostFixture _hosts;
    private readonly MemoryPreferenceStore _store = MemoryPreferenceStore.Shared;

    public UserPreferencesEndpointsTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        _hosts.Reset();
        _store.Values.Clear();
    }

    [Fact]
    public async Task A_locked_view_refuses_a_per_view_layout_and_stores_nothing()
    {
        var (client, _) = await AgentAsync();

        var res = await client.PutAsJsonAsync($"/api/preferences/columns?viewId={LockedView}", new { columns = "number,subject" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Empty(_store.Values);
    }

    [Fact]
    public async Task An_open_view_stores_the_layout_under_the_callers_own_key()
    {
        var (client, userId) = await AgentAsync();

        var res = await client.PutAsJsonAsync($"/api/preferences/columns?viewId={OpenView}", new { columns = "number,subject" });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("number,subject", _store.Values[(userId, $"columns:view:{OpenView}")]);
    }

    [Fact]
    public async Task A_locked_view_reads_the_view_columns_even_with_a_user_layout_stored()
    {
        var (client, userId) = await AgentAsync();
        _store.Values[(userId, $"columns:view:{LockedView}")] = "subject";
        _store.Values[(userId, "columns")] = "subject";

        var pref = await client.GetFromJsonAsync<ColumnPref>($"/api/preferences/columns?viewId={LockedView}");

        Assert.Equal(new ColumnPref("number,status", "view", true), pref);
    }

    [Fact]
    public async Task Notification_mail_writes_only_the_switches_sent()
    {
        var (client, userId) = await AgentAsync();

        var res = await client.PutAsJsonAsync("/api/preferences/notification-mail", new { likes = true });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var entry = Assert.Single(_store.Values);
        Assert.Equal(userId, entry.Key.UserId);
        Assert.Equal("true", entry.Value);
    }

    // ---- plumbing -----------------------------------------------------------

    private sealed record ColumnPref(string Columns, string Source, bool Locked);

    private async Task<(HttpClient Client, Guid UserId)> AgentAsync()
    {
        var userId = Guid.NewGuid();
        var host = _hosts.Host("preferences-fakes", b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IUserPreferenceStore>();
            services.AddSingleton<IUserPreferenceStore>(MemoryPreferenceStore.Shared);
            services.RemoveAll<IViewRepository>();
            services.AddSingleton(Fake<IViewRepository>.Create((m, a) => m.Name == nameof(IViewRepository.GetAsync)
                ? Task.FromResult(ViewFor((Guid)a![0]!))
                : throw new InvalidOperationException("Unexpected: " + m.Name)));
        }));
        return (await _hosts.Base.SignedInClientAsync("Agent", host, userId), userId);
    }

    private static View? ViewFor(Guid id) => id == LockedView || id == OpenView
        ? new View(id, Guid.NewGuid(), "View", "{}", "number,status", 0, true, "{}",
            DateTime.UtcNow, DateTime.UtcNow, AllowUserColumns: id == OpenView)
        : null;

    /// One store for the derived host (built once per fixture); each test
    /// clears it in the constructor.
    private sealed class MemoryPreferenceStore : IUserPreferenceStore
    {
        public static readonly MemoryPreferenceStore Shared = new();
        public ConcurrentDictionary<(Guid UserId, string Key), string> Values { get; } = new();

        public Task<string?> GetAsync(Guid userId, string key, CancellationToken ct) =>
            Task.FromResult(Values.TryGetValue((userId, key), out var v) ? v : null);

        public Task<IReadOnlyDictionary<string, string>> GetManyAsync(Guid userId, IReadOnlyCollection<string> keys, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Values
                .Where(e => e.Key.UserId == userId && keys.Contains(e.Key.Key))
                .ToDictionary(e => e.Key.Key, e => e.Value));

        public Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(Guid userId, string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Values
                .Where(e => e.Key.UserId == userId && e.Key.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(e => e.Key.Key, e => e.Value));

        public Task SetAsync(Guid userId, string key, string value, CancellationToken ct)
        {
            Values[(userId, key)] = value;
            return Task.CompletedTask;
        }

        public Task SetManyAsync(Guid userId, IReadOnlyDictionary<string, string> values, CancellationToken ct)
        {
            foreach (var (k, v) in values) Values[(userId, k)] = v;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid userId, string key, CancellationToken ct)
        {
            Values.TryRemove((userId, key), out _);
            return Task.CompletedTask;
        }
    }
}
