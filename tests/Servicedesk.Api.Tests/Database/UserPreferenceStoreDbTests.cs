using Dapper;
using Servicedesk.Infrastructure.Preferences;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The SQL behind /api/preferences: per-user isolation, the literal prefix
/// match, and the bulk upsert that leaves unchanged rows alone (v0.1.25).
[Collection(PostgresCollection.Name)]
public sealed class UserPreferenceStoreDbTests
{
    private readonly PostgresDatabase _db;

    public UserPreferenceStoreDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Values_are_per_user_and_can_be_overwritten_and_deleted()
    {
        var store = new UserPreferenceStore(_db.DataSource);
        var seed = new DbSeed(_db.DataSource);
        var (a, b) = (await seed.UserAsync(), await seed.UserAsync());

        await store.SetAsync(a, "ui:theme", "dark", default);
        await store.SetAsync(a, "ui:theme", "light", default);

        Assert.Equal("light", await store.GetAsync(a, "ui:theme", default));
        Assert.Null(await store.GetAsync(b, "ui:theme", default));

        await store.DeleteAsync(a, "ui:theme", default);
        Assert.Null(await store.GetAsync(a, "ui:theme", default));
    }

    [PostgresFact]
    public async Task Prefix_and_key_list_reads_return_only_the_matching_keys_of_that_user()
    {
        var store = new UserPreferenceStore(_db.DataSource);
        var seed = new DbSeed(_db.DataSource);
        var (a, b) = (await seed.UserAsync(), await seed.UserAsync());
        await store.SetManyAsync(a, new Dictionary<string, string>
        {
            ["workspace:tabs"] = "1",
            ["workspace:split"] = "2",
            ["workspace_other"] = "x", // no colon: not under the prefix
            ["columns"] = "number",
        }, default);
        await store.SetAsync(b, "workspace:tabs", "other user", default);

        var workspace = await store.GetByPrefixAsync(a, "workspace:", default);
        Assert.Equal(new[] { "workspace:split", "workspace:tabs" }, workspace.Keys.Order());

        var some = await store.GetManyAsync(a, ["columns", "missing"], default);
        Assert.Equal("number", Assert.Single(some).Value);
    }

    [PostgresFact]
    public async Task A_bulk_save_rewrites_only_changed_values()
    {
        var store = new UserPreferenceStore(_db.DataSource);
        var user = await new DbSeed(_db.DataSource).UserAsync();
        await store.SetManyAsync(user, new Dictionary<string, string> { ["workspace:a"] = "1", ["workspace:b"] = "1" }, default);
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE user_preferences SET updated_utc = '2020-06-15T12:00:00Z' WHERE user_id = @user", new { user });

        await store.SetManyAsync(user, new Dictionary<string, string> { ["workspace:a"] = "1", ["workspace:b"] = "2" }, default);

        var stamps = (await c.QueryAsync<(string Key, DateTime Updated)>(
            "SELECT pref_key, updated_utc FROM user_preferences WHERE user_id = @user", new { user }))
            .ToDictionary(r => r.Key, r => r.Updated.Year);
        Assert.Equal(2020, stamps["workspace:a"]);
        Assert.NotEqual(2020, stamps["workspace:b"]);
        Assert.Equal("2", await store.GetAsync(user, "workspace:b", default));
    }
}
