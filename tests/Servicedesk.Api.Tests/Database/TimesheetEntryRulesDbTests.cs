using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Servicedesk.Infrastructure.Timesheet;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The one set of timesheet entry rules, against real SQL. *My day* and
/// *+ Log time* on a ticket both post to <c>/api/timesheet/entries</c>, so
/// these rules are what both surfaces get — the front-end copies in
/// <c>lib/timesheet-api.ts</c> are convenience, the server decides.
[Collection(PostgresCollection.Name)]
public sealed class TimesheetEntryRulesDbTests
{
    private static readonly DateOnly Day = new(2026, 11, 23);
    private readonly PostgresDatabase _db;

    public TimesheetEntryRulesDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task A_valid_entry_is_stored_with_its_minutes_and_a_trimmed_description()
    {
        var (svc, user, task, ticket) = await SetupAsync();

        var result = await svc.CreateAsync(user, Input(task, ticket, 540, 615, "  Printer fixed  "));

        var row = Assert.IsType<CreateTimesheetEntryResult.Created>(result).Row;
        Assert.Equal(75, row.Minutes);
        Assert.Equal("Printer fixed", row.Description);
        Assert.Equal(ticket, row.TicketId);
        Assert.Equal(user, row.UserId);
    }

    [PostgresTheory]
    [InlineData(-1, 60, "startMinutes")]
    [InlineData(0, 1441, "endMinutes")]
    [InlineData(600, 600, "endMinutes")]
    [InlineData(600, 540, "endMinutes")]
    public async Task Times_must_lie_within_the_day_and_end_after_start(int start, int end, string field)
    {
        var (svc, user, task, ticket) = await SetupAsync();

        var result = await svc.CreateAsync(user, Input(task, ticket, start, end));

        Assert.Contains(Errors(result), e => e.Field == field);
        Assert.Equal(0, await EntryCountAsync(user));
    }

    [PostgresFact]
    public async Task A_description_is_required()
    {
        var (svc, user, task, ticket) = await SetupAsync();

        var result = await svc.CreateAsync(user, Input(task, ticket, 540, 600, "   "));

        Assert.Contains(Errors(result), e => e.Field == "description");
    }

    [PostgresFact]
    public async Task A_task_is_required_and_must_not_be_archived()
    {
        var (svc, user, _, ticket) = await SetupAsync();
        var archived = await TaskAsync(requiresTicket: false, archived: true);

        var none = await svc.CreateAsync(user, Input(Guid.Empty, ticket, 540, 600));
        var gone = await svc.CreateAsync(user, Input(archived, ticket, 540, 600));
        var unknown = await svc.CreateAsync(user, Input(Guid.NewGuid(), ticket, 540, 600));

        Assert.Contains(Errors(none), e => e.Field == "taskId");
        Assert.Contains(Errors(gone), e => e.Field == "taskId");
        Assert.Contains(Errors(unknown), e => e.Field == "taskId");
    }

    [PostgresFact]
    public async Task A_task_that_requires_a_ticket_refuses_an_entry_without_one()
    {
        var (svc, user, task, _) = await SetupAsync();

        var result = await svc.CreateAsync(user, Input(task, ticketId: null, 540, 600));

        Assert.Contains(Errors(result), e => e.Field == "ticketId");
    }

    [PostgresFact]
    public async Task A_task_without_the_ticket_requirement_accepts_no_ticket_but_still_checks_one_given()
    {
        var (svc, user, _, _) = await SetupAsync();
        var admin = await TaskAsync(requiresTicket: false);

        var free = await svc.CreateAsync(user, Input(admin, ticketId: null, 540, 600));
        var bogus = await svc.CreateAsync(user, Input(admin, Guid.NewGuid(), 600, 660));

        Assert.IsType<CreateTimesheetEntryResult.Created>(free);
        Assert.Contains(Errors(bogus), e => e.Field == "ticketId");
    }

    [PostgresFact]
    public async Task A_deleted_ticket_counts_as_missing()
    {
        var (svc, user, task, ticket) = await SetupAsync();
        await ExecAsync("UPDATE tickets SET is_deleted = TRUE WHERE id = @ticket", new { ticket });

        var result = await svc.CreateAsync(user, Input(task, ticket, 540, 600));

        Assert.Contains(Errors(result), e => e.Field == "ticketId");
    }

    [PostgresFact]
    public async Task A_merged_ticket_points_to_the_surviving_ticket()
    {
        var (svc, user, task, ticket) = await SetupAsync();
        var seed = new DbSeed(_db.DataSource);
        var (queue, status, priority) = await seed.TaxonomyAsync();
        var survivor = await seed.TicketAsync("Survivor", await seed.ContactAsync(), queue, status, priority);
        await ExecAsync("UPDATE tickets SET merged_into_ticket_id = @survivor WHERE id = @ticket", new { ticket, survivor });
        var number = await ScalarAsync<long>("SELECT number FROM tickets WHERE id = @survivor", new { survivor });

        var result = await svc.CreateAsync(user, Input(task, ticket, 540, 600));

        var error = Assert.Single(Errors(result), e => e.Field == "ticketId");
        Assert.Contains($"#{number}", error.Message);
    }

    [PostgresFact]
    public async Task Update_applies_the_same_rules()
    {
        var (svc, user, task, ticket) = await SetupAsync();
        var created = (CreateTimesheetEntryResult.Created)await svc.CreateAsync(user, Input(task, ticket, 540, 600));

        var invalid = await svc.UpdateAsync(created.Row.Id, user, Input(task, ticket, 600, 540, ""));
        var valid = await svc.UpdateAsync(created.Row.Id, user, Input(task, ticket, 540, 630));

        var failed = Assert.IsType<UpdateTimesheetEntryResult.ValidationFailed>(invalid);
        Assert.Contains(failed.Errors, e => e.Field == "endMinutes");
        Assert.Contains(failed.Errors, e => e.Field == "description");
        Assert.Equal(90, Assert.IsType<UpdateTimesheetEntryResult.Updated>(valid).Row.Minutes);
    }

    [PostgresFact]
    public async Task Another_users_entry_can_be_neither_read_changed_nor_deleted()
    {
        var (svc, owner, task, ticket) = await SetupAsync();
        var other = await new DbSeed(_db.DataSource).UserAsync();
        var created = (CreateTimesheetEntryResult.Created)await svc.CreateAsync(owner, Input(task, ticket, 540, 600));
        var id = created.Row.Id;

        Assert.Null(await svc.GetForUserAsync(id, other));
        Assert.IsType<UpdateTimesheetEntryResult.NotFound>(await svc.UpdateAsync(id, other, Input(task, ticket, 540, 700)));
        Assert.False(await svc.DeleteOwnAsync(id, other));
        Assert.Empty(await svc.ListByDateAsync(other, Day));

        var mine = Assert.Single(await svc.ListByDateAsync(owner, Day));
        Assert.Equal(60, mine.Minutes);
        Assert.True(await svc.DeleteOwnAsync(id, owner));
    }

    // ---- plumbing -----------------------------------------------------------

    private static TimesheetEntryInput Input(
        Guid taskId, Guid? ticketId, int start, int end, string description = "Work done") =>
        new(Day, start, end, taskId, ticketId, description);

    private static IReadOnlyList<TimesheetFieldError> Errors(CreateTimesheetEntryResult result) =>
        Assert.IsType<CreateTimesheetEntryResult.ValidationFailed>(result).Errors;

    /// A fresh service (own cache, so a task inserted by the test is seen),
    /// an agent, a task that requires a ticket, and a ticket.
    private async Task<(TimesheetEntryService Svc, Guid User, Guid Task, Guid Ticket)> SetupAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (queue, status, priority) = await seed.TaxonomyAsync();
        var ticket = await seed.TicketAsync("Timesheet target", await seed.ContactAsync(), queue, status, priority);
        var user = await seed.UserAsync();
        var task = await TaskAsync(requiresTicket: true);
        var svc = new TimesheetEntryService(
            _db.DataSource, new TimesheetTaskService(_db.DataSource, new MemoryCache(new MemoryCacheOptions())));
        return (svc, user, task, ticket);
    }

    private Task<Guid> TaskAsync(bool requiresTicket, bool archived = false) => ScalarAsync<Guid>(
        "INSERT INTO timesheet_tasks (name, requires_ticket, archived) VALUES (@name, @requiresTicket, @archived) RETURNING id",
        new { name = "Task " + Guid.NewGuid().ToString("N")[..8], requiresTicket, archived });

    private async Task<int> EntryCountAsync(Guid user) =>
        await ScalarAsync<int>("SELECT count(*) FROM timesheet_entries WHERE user_id = @user", new { user });

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("No value.");
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }
}
