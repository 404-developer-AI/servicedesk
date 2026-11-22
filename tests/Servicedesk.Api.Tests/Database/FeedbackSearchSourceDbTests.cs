using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for Employee Feedback: full-access flag holders and
/// admins find every entry, "own only" holders find just the entries they
/// created, everybody else (and every customer) finds nothing.
[Collection(PostgresCollection.Name)]
public sealed class FeedbackSearchSourceDbTests
{
    private readonly PostgresDatabase _db;

    public FeedbackSearchSourceDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Feedback_scope_follows_the_per_user_flag()
    {
        var token = Token();
        var seed = new DbSeed(_db.DataSource);
        var target = await seed.UserAsync();
        var authorA = await seed.UserAsync();
        var authorB = await seed.UserAsync();
        Guid entryA, entryB;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            const string sql = """
                INSERT INTO feedback_entries (target_user_id, body_html, created_by_user_id)
                VALUES (@target, @body, @author)
                RETURNING id
                """;
            entryA = await c.ExecuteScalarAsync<Guid>(sql, new { target, body = $"<p>{token} by author A</p>", author = authorA });
            entryB = await c.ExecuteScalarAsync<Guid>(sql, new { target, body = $"<p>{token} by author B</p>", author = authorB });
        }

        var source = new FeedbackSearchSource(_db.DataSource);
        var both = Sorted(entryA, entryB);

        Assert.Equal(both, Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(both, Ids(await source.SearchAsync(Query(token), Agent(SearchFeature.Feedback), default)));

        // Own-only: just the entries this user wrote.
        var ownA = AgentWithId(authorA, SearchFeature.FeedbackOwnOnly);
        Assert.Equal(Sorted(entryA), Ids(await source.SearchAsync(Query(token), ownA, default)));
        var ownB = AgentWithId(authorB, SearchFeature.FeedbackOwnOnly);
        Assert.Equal(Sorted(entryB), Ids(await source.SearchAsync(Query(token), ownB, default)));

        // Own-only without having written anything: the SQL runs and returns nothing.
        var bystander = Agent(SearchFeature.FeedbackOwnOnly);
        Assert.True(source.IsAvailableFor(bystander));
        Assert.Empty((await source.SearchAsync(Query(token), bystander, default)).Hits);

        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, Agent(SearchFeature.Contracts, SearchFeature.Assets), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.Feedback), token);
        await AssertNoHitsAsync(source, Customer(SearchFeature.FeedbackOwnOnly), token);
    }
}
