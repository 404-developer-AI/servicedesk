using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class SettingsServiceExtensionsTests
{
    private sealed class FailingSettings(Exception error) : ISettingsService
    {
        public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromException<T>(error);
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }

    [Fact]
    public async Task Returns_the_stored_value_when_the_read_works()
    {
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Sla.Enabled, "false");

        Assert.False(await settings.GetOrAsync(SettingKeys.Sla.Enabled, fallback: true));
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Returns_the_fallback_when_the_read_fails(Type errorType)
    {
        var settings = new FailingSettings((Exception)Activator.CreateInstance(errorType)!);

        Assert.Equal(42, await settings.GetOrAsync("Any.Key", 42));
    }

    [Fact]
    public async Task A_cancelled_read_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var settings = new FailingSettings(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => settings.GetOrAsync("Any.Key", 42, cts.Token));
    }
}
