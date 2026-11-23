using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// CLAUDE.md → SLA master switch: `Sla.Enabled` off = no calculation
/// anywhere and no SLA triggers, while existing SLA data is kept. Each test
/// also runs the same path with the switch on, so it proves it can see the
/// work it asserts is skipped.
[Collection(PostgresCollection.Name)]
public sealed class SlaMasterSwitchDbTests
{
    private readonly PostgresDatabase _db;

    public SlaMasterSwitchDbTests(PostgresDatabase db) => _db = db;

    private SlaEngine Engine(bool enabled)
    {
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Sla.Enabled, enabled ? "true" : "false");
        return new SlaEngine(_db.DataSource, new SlaRepository(_db.DataSource), new BusinessHoursCalculator(),
            settings, NullLogger<SlaEngine>.Instance);
    }

    private async Task<Guid> TicketAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (queue, status, priority) = await seed.TaxonomyAsync();
        return await seed.TicketAsync("SLA switch", await seed.ContactAsync(), queue, status, priority);
    }

    private async Task<int> StateRowsAsync(Guid ticketId)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ticket_sla_state WHERE ticket_id = @ticketId", new { ticketId });
    }

    [PostgresFact]
    public async Task Off_no_ticket_is_calculated_on_writes_or_in_the_sweep()
    {
        var ticket = await TicketAsync();
        var off = Engine(enabled: false);

        Assert.False(await off.IsEnabledAsync(default));
        await off.OnTicketCreatedAsync(ticket, default);
        await off.OnTicketEventAsync(ticket, "Note", default);
        await off.OnTicketFieldsChangedAsync(ticket, default);
        Assert.Equal(0, await off.RecalcManyAsync(new[] { ticket }, default));
        Assert.Equal(0, await StateRowsAsync(ticket));

        // Positive control: the same calls with the switch on write state.
        var on = Engine(enabled: true);
        await on.RecalcAsync(ticket, default);
        Assert.Equal(1, await StateRowsAsync(ticket));
        Assert.Equal(1, await on.RecalcManyAsync(new[] { ticket }, default));
    }

    [PostgresFact]
    public async Task Off_keeps_existing_sla_state()
    {
        var ticket = await TicketAsync();
        await Engine(enabled: true).RecalcAsync(ticket, default);
        Assert.Equal(1, await StateRowsAsync(ticket));

        var off = Engine(enabled: false);
        await off.RecalcAsync(ticket, default);
        await off.RecalcManyAsync(new[] { ticket }, default);

        Assert.Equal(1, await StateRowsAsync(ticket));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task Sla_escalation_and_warning_triggers_only_run_while_on(bool enabled, int expectedCandidateReads)
    {
        var reads = new List<string>();
        var repo = Fake<ITriggerRepository>.Create((method, _) =>
        {
            if (method.Name is "ListEscalationCandidatesAsync" or "ListEscalationWarningCandidatesAsync")
            {
                reads.Add(method.Name);
                return EmptyListTask(method.ReturnType);
            }
            throw new InvalidOperationException($"Unexpected call: {method.Name}");
        });
        var triggers = Fake<ITriggerService>.Create(
            (method, _) => throw new InvalidOperationException($"Unexpected call: {method.Name}"));
        var sla = Fake<ISlaEngine>.Create((method, _) => method.Name == "IsEnabledAsync"
            ? Task.FromResult(enabled)
            : throw new InvalidOperationException($"Unexpected call: {method.Name}"));

        await TriggerSchedulerWorker.RunEscalationsAsync(repo, triggers, new InMemorySettingsService(), sla, default);

        Assert.Equal(expectedCandidateReads, reads.Count);
    }

    /// Task.FromResult of an empty list for a `Task<IReadOnlyList<T>>` return type.
    private static object EmptyListTask(Type taskType)
    {
        var listType = taskType.GetGenericArguments()[0];
        var itemType = listType.GetGenericArguments()[0];
        var empty = Array.CreateInstance(itemType, 0);
        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(listType).Invoke(null, new object[] { empty })!;
    }
}
