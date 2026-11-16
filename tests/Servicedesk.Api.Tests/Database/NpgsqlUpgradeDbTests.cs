using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dapper;
using Servicedesk.Infrastructure.Performance;
using Servicedesk.Infrastructure.Persistence;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// Pins the two things the Npgsql 8 → 10 upgrade changed underneath us
/// without any compile error:
///   1. `date` columns read as DateTime again (Npgsql 10 defaults to
///      DateOnly), via the legacy resolver in ServicedeskDataSource;
///   2. the meter / activity names PerfInstrumentation listens to (Npgsql 10
///      renamed them to the OpenTelemetry conventions — a stale name means
///      the Performance page silently shows no database data).
[Collection(PostgresCollection.Name)]
public sealed class NpgsqlUpgradeDbTests
{
    private readonly PostgresDatabase _db;

    public NpgsqlUpgradeDbTests(PostgresDatabase db) => _db = db;

    private sealed class DateRow
    {
        public DateTime Day { get; set; }
        public DateTime? Missing { get; set; }
    }

    [PostgresFact]
    public async Task Date_columns_read_as_DateTime()
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync();

        var scalar = await conn.ExecuteScalarAsync<object>("SELECT DATE '2026-11-16'");
        Assert.IsType<DateTime>(scalar);
        Assert.Equal(new DateTime(2026, 11, 16), (DateTime)scalar!);

        var row = await conn.QuerySingleAsync<DateRow>("SELECT DATE '2026-03-01' AS Day, NULL::date AS Missing");
        Assert.Equal(new DateTime(2026, 3, 1), row.Day);
        Assert.Null(row.Missing);

        var days = (await conn.QueryAsync<DateTime>(
            "SELECT d::date FROM generate_series(DATE '2026-01-30', DATE '2026-02-01', INTERVAL '1 day') d")).ToList();
        Assert.Equal(new DateTime(2026, 2, 1), days[^1]);

        // Writing a DateTime parameter into a date column still works.
        var echoed = await conn.ExecuteScalarAsync<DateTime>("SELECT @d::date", new { d = new DateTime(2026, 5, 4) });
        Assert.Equal(new DateTime(2026, 5, 4), echoed);
    }

    [PostgresFact]
    public async Task Perf_instrumentation_recognises_every_npgsql_instrument_it_needs()
    {
        var seen = new ConcurrentDictionary<PerfInstrumentation.Instrument, bool>();
        var stateTags = new ConcurrentBag<string>();
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != "Npgsql") return;
                if (PerfInstrumentation.Classify(instrument) is { } id)
                {
                    seen[id] = true;
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<int>((i, v, tags, s) => Collect(tags));
        meters.SetMeasurementEventCallback<long>((i, v, tags, s) => Collect(tags));
        meters.Start();

        await using (var conn = await _db.DataSource.OpenConnectionAsync())
            await conn.ExecuteScalarAsync<int>("SELECT 1");
        meters.RecordObservableInstruments();

        Assert.True(seen.ContainsKey(PerfInstrumentation.Instrument.DbCommandDuration), "command duration");
        Assert.True(seen.ContainsKey(PerfInstrumentation.Instrument.DbCommandFailed), "command failed");
        Assert.True(seen.ContainsKey(PerfInstrumentation.Instrument.DbConnectionUsage), "pool usage");
        Assert.True(seen.ContainsKey(PerfInstrumentation.Instrument.DbConnectionMax), "pool max");
        Assert.True(seen.ContainsKey(PerfInstrumentation.Instrument.DbPendingRequests), "pending requests");
        Assert.True(seen.ContainsKey(PerfInstrumentation.Instrument.DbConnectionTimeouts), "pool timeouts");
        // The pool gauge splits used/idle on this tag.
        Assert.Contains("db.client.connection.state", stateTags);

        void Collect(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var t in tags) stateTags.Add(t.Key);
        }
    }

    [PostgresFact]
    public async Task Command_activities_carry_the_statement_and_connects_are_not_traced()
    {
        var stopped = new ConcurrentBag<global::System.Diagnostics.Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        // A fresh data source, so the open below is a real physical connect.
        var marker = $"SELECT 'npgsql-upgrade-{Guid.NewGuid():N}'";
        await using (var fresh = ServicedeskDataSource.Create(
                         Environment.GetEnvironmentVariable(PostgresDatabase.ConnectionStringVariable)!))
        await using (var conn = await fresh.OpenConnectionAsync())
            await conn.ExecuteScalarAsync<string>(marker);

        var mine = stopped.Where(a => a.GetTagItem("db.query.text") as string == marker).ToList();
        Assert.Single(mine);
        Assert.DoesNotContain(stopped, a => a.OperationName.StartsWith("CONNECT", StringComparison.Ordinal));
    }
}
