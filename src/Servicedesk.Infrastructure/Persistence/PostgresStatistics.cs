using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Persistence;

/// v0.0.101 — refresh planner statistics after a bulk load.
///
/// Autovacuum's ANALYZE trigger is threshold-based (default: 10% of the
/// table + 50 rows changed), so right after a large one-shot import
/// (Zammad tickets / KB, timesheet migration) the planner keeps working
/// with stale row counts and histograms until autovacuum catches up —
/// exactly the window in which the admin is clicking around to verify the
/// import. An explicit ANALYZE on the touched tables is cheap (it samples,
/// it does not scan) and closes that window.
///
/// Best-effort by design: statistics are an optimisation, never a
/// correctness concern, so a failure is logged and swallowed. Table names
/// come from a fixed whitelist — never from user input.
public static class PostgresStatistics
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "tickets", "ticket_bodies", "ticket_events", "ticket_event_search",
        "attachments", "contacts", "companies", "contact_companies",
        "kb_sections", "kb_section_translations", "kb_articles", "kb_article_translations",
        "timesheet_entries",
    };

    public static async Task AnalyzeAsync(
        NpgsqlDataSource dataSource,
        ILogger logger,
        CancellationToken ct,
        params string[] tables)
    {
        var safe = tables.Where(Allowed.Contains).Distinct().ToArray();
        if (safe.Length == 0) return;
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            foreach (var table in safe)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "ANALYZE " + table, cancellationToken: ct));
            }
            logger.LogInformation("ANALYZE refreshed planner statistics for {Tables}", string.Join(", ", safe));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ANALYZE after bulk load failed for {Tables} — continuing (statistics only)", string.Join(", ", safe));
        }
    }
}
