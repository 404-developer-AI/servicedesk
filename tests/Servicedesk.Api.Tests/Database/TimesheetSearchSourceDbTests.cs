using Dapper;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for the timesheet: a user finds only their own
/// entries, a per-user timesheet manager finds everyone's, customers nothing.
[Collection(PostgresCollection.Name)]
public sealed class TimesheetSearchSourceDbTests
{
    private readonly PostgresDatabase _db;

    public TimesheetSearchSourceDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Users_find_their_own_entries_and_managers_find_everyones()
    {
        var token = Token();
        var seed = new DbSeed(_db.DataSource);
        var userA = await seed.UserAsync();
        var userB = await seed.UserAsync();
        var manager = await seed.UserAsync();
        var bystander = await seed.UserAsync();
        Guid entryA, entryB;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE users SET timesheet_manager = TRUE WHERE id = @manager", new { manager });
            var taskId = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO timesheet_tasks (name, requires_ticket) VALUES (@name, FALSE) RETURNING id",
                new { name = $"Example task {token}" });
            const string sql = """
                INSERT INTO timesheet_entries (user_id, entry_date, start_minutes, end_minutes, minutes, task_id, description)
                VALUES (@userId, current_date, 540, 600, 60, @taskId, @description)
                RETURNING id
                """;
            entryA = await c.ExecuteScalarAsync<Guid>(sql, new { userId = userA, taskId, description = $"{token} work by A" });
            entryB = await c.ExecuteScalarAsync<Guid>(sql, new { userId = userB, taskId, description = $"{token} work by B" });
        }

        var source = new TimesheetSearchSource(_db.DataSource);

        Assert.Equal(Sorted(entryA), Ids(await source.SearchAsync(Query(token), AgentWithId(userA), default)));
        Assert.Equal(Sorted(entryB), Ids(await source.SearchAsync(Query(token), AgentWithId(userB), default)));
        Assert.Equal(Sorted(entryA, entryB), Ids(await source.SearchAsync(Query(token), AgentWithId(manager), default)));

        // An agent with no entries of their own sees nothing, even though the rows exist.
        Assert.Empty((await source.SearchAsync(Query(token), AgentWithId(bystander), default)).Hits);

        await AssertNoHitsAsync(source, Customer(), token);
    }
}
