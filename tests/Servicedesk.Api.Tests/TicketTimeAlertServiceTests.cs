using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Timesheet;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The per-ticket hour-limit alert raises a ticket's budget only when the
/// agent has ticked the mandatory customer-confirmation box. That tick is
/// re-checked server-side — never trusted from the client alone. These tests
/// pin the two guards that run before any database access: an unconfirmed or
/// non-positive request is rejected without ever touching the connection.
public sealed class TicketTimeAlertServiceTests
{
    [Fact]
    public async Task Extend_without_customer_confirmation_is_rejected()
    {
        var service = Build();

        var result = await service.ExtendAsync(
            Guid.NewGuid(), Guid.NewGuid(), addMinutes: 60, customerConfirmed: false, note: null);

        Assert.Equal(TicketTimeAlertExtendResult.NotConfirmed, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(1_000_000)]
    public async Task Extend_with_invalid_minutes_is_rejected(int minutes)
    {
        var service = Build();

        // Even with the confirmation tick set, an out-of-range amount never
        // reaches the database.
        var result = await service.ExtendAsync(
            Guid.NewGuid(), Guid.NewGuid(), addMinutes: minutes, customerConfirmed: true, note: null);

        Assert.Equal(TicketTimeAlertExtendResult.InvalidMinutes, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Disable_without_reason_is_rejected(string reason)
    {
        var service = Build();

        // The mandatory "why are you disabling" reason is re-checked here, so a
        // blank one is rejected before any database access.
        var result = await service.DisableAsync(Guid.NewGuid(), Guid.NewGuid(), reason);

        Assert.Equal(TicketTimeAlertDisableResult.ReasonRequired, result);
    }

    private static TicketTimeAlertService Build()
    {
        // Lazy data source — never connected because every guarded path
        // short-circuits before opening a connection. Construction alone does
        // not connect. The ticket repository is likewise never reached on these
        // guarded paths, so a null stand-in is safe here.
        var ds = NpgsqlDataSource.Create("Host=localhost;Username=u;Password=p;Database=d");
        return new TicketTimeAlertService(
            ds, new StubSettings(), tickets: null!,
            NullLogger<TicketTimeAlertService>.Instance);
    }

    private sealed class StubSettings : ISettingsService
    {
        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(default(T)!);

        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }
}
