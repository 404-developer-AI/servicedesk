using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Servicedesk.Infrastructure.Persistence;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// A throwaway PostgreSQL database for tests that must run real SQL — the
/// queries that decide who sees which rows (portal whitelist, queue access,
/// search). Built once per test run from the production schema
/// (`DatabaseBootstrapper`) and dropped afterwards.
///
/// Configuration: `SERVICEDESK_TEST_PG` holds a connection string for a
/// role that may CREATE DATABASE (locally: your dev PostgreSQL; in CI: the
/// postgres service of the backend job). Without it the tests are skipped
/// locally; in CI (`CI=true`) or with `SERVICEDESK_TEST_PG_REQUIRED=1` a
/// missing database fails the run instead, so the safety net can't silently
/// switch itself off.
public sealed class PostgresDatabase : IAsyncLifetime
{
    public const string ConnectionStringVariable = "SERVICEDESK_TEST_PG";

    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } cs ? cs : null;

    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("SERVICEDESK_TEST_PG_REQUIRED") == "1";

    private string? _databaseName;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var admin = AdminConnectionString;
        if (admin is null)
        {
            if (Required)
                throw new InvalidOperationException(
                    $"{ConnectionStringVariable} is not set, but database tests are required in this environment.");
            return; // Tests are skipped by PostgresFactAttribute.
        }

        // The name carries its creation time (UTC) so a run that died before
        // DisposeAsync leaves a database the next run can recognise as stale.
        _databaseName = $"sd_test_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await DropStaleDatabasesAsync(conn);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = _databaseName };
        DataSource = ServicedeskDataSource.Create(builder.ConnectionString);
        await new DatabaseBootstrapper(DataSource, NullLogger<DatabaseBootstrapper>.Instance)
            .StartAsync(CancellationToken.None);
    }

    /// Drops leftovers of crashed runs: test databases older than an hour.
    /// Younger ones may belong to a run in progress and are left alone.
    private static async Task DropStaleDatabasesAsync(NpgsqlConnection conn)
    {
        var cutoff = DateTime.UtcNow.AddHours(-1).ToString("yyyyMMddHHmmss");
        var stale = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE datname LIKE 'sd_test_%'", conn))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                // sd_test_<14-digit stamp>_…; names without a stamp predate it.
                var stamp = name.Length >= 22 && name[22..].StartsWith('_') ? name[8..22] : "";
                if (stamp.Length == 0 || string.CompareOrdinal(stamp, cutoff) < 0) stale.Add(name);
            }
        }
        foreach (var name in stale)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_databaseName is null) return;
        await DataSource.DisposeAsync();
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabase>
{
    public const string Name = "postgres";
}

/// A [Fact] that runs only when a test database is configured (see
/// <see cref="PostgresDatabase"/>). Where the database is required, it is
/// never skipped — the fixture fails instead.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresDatabase.AdminConnectionString is null && !PostgresDatabase.Required)
            Skip = $"Set {PostgresDatabase.ConnectionStringVariable} to run database tests.";
    }
}

/// The [Theory] counterpart of <see cref="PostgresFactAttribute"/>.
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (PostgresDatabase.AdminConnectionString is null && !PostgresDatabase.Required)
            Skip = $"Set {PostgresDatabase.ConnectionStringVariable} to run database tests.";
    }
}
