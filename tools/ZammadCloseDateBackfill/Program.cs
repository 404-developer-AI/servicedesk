using System.Globalization;
using System.Text;
using System.Text.Json;

// =====================================================================
// Zammad close-date backfill — one-time repair for imported tickets.
//
// Why this exists
// ---------------
// The Zammad import never carried the upstream close/resolved date onto
// the local ticket: imported rows got `tickets.created_utc = now()` (the
// import day) and no `StatusChange` timeline event. The back-office
// Resolved / CWI tabs (and the timesheet rollup) filter on "when did the
// ticket enter its current status" = the most recent StatusChange INTO
// the current status, falling back to created_utc. With neither present,
// every imported closed/resolved ticket falls back to the import day, so
// they all show up under "this month".
//
// What this tool does
// -------------------
// 1. Talks ONLY to Zammad (no database connection needed). It pages
//    through every ticket and collects each one's real close date
//    (`close_at`); tickets that were never closed are ignored.
// 2. Emits an idempotent, reviewable .sql file that, when YOU run it
//    against the Servicedesk database:
//      a. inserts a synthetic StatusChange event dated at the real close
//         date (only when one doesn't already exist) — this is what the
//         Resolved/CWI/timesheet month-filter reads;
//      b. fills resolved_utc / closed_utc with the same date, only when
//         still NULL, to keep the rest of the app consistent.
//    The SQL self-filters: it joins on zammad_ticket_id and only touches
//    locally-imported tickets that are currently in a Resolved/Closed
//    status. App-native tickets are never affected, and a close date for
//    a Zammad ticket that was never imported is simply ignored. It leaves
//    merged_utc / merged_into_ticket_id alone (in-app merge feature).
//
// The tool itself NEVER writes to any database. It only produces SQL for
// you to inspect and apply where Postgres is reachable.
//
// Usage
//   zammad-close-date-backfill \
//       --zammad-url   https://support.example.com \
//       --zammad-token <api-token> \
//       [--out ./manual-logs/zammad-close-date-backfill.sql] \
//       [--page-size 100]
//
// Values also read from env: ZD_ZAMMAD_URL / ZD_ZAMMAD_TOKEN.
// =====================================================================

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var opts = Options.Parse(args);
    if (opts.ShowHelp)
    {
        Options.PrintUsage();
        return 0;
    }
    return await Backfill.RunAsync(opts, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("\nCancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nERROR: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------

sealed class Options
{
    public string? ZammadUrl { get; private set; }
    public string? ZammadToken { get; private set; }
    public string OutPath { get; private set; } = Path.Combine("manual-logs", "zammad-close-date-backfill.sql");
    public int PageSize { get; private set; } = 100;
    public bool ShowHelp { get; private set; }

    public string RequireZammadUrl() => ZammadUrl ?? throw new ArgumentException("Missing --zammad-url / ZD_ZAMMAD_URL.");
    public string RequireZammadToken() => ZammadToken ?? throw new ArgumentException("Missing --zammad-token / ZD_ZAMMAD_TOKEN.");

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--zammad-url": o.ZammadUrl = Next(args, ref i); break;
                case "--zammad-token": o.ZammadToken = Next(args, ref i); break;
                case "--out": o.OutPath = Next(args, ref i); break;
                case "--page-size": o.PageSize = Math.Clamp(int.Parse(Next(args, ref i), CultureInfo.InvariantCulture), 1, 200); break;
                case "-h" or "--help": o.ShowHelp = true; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        o.ZammadUrl ??= Environment.GetEnvironmentVariable("ZD_ZAMMAD_URL");
        o.ZammadToken ??= Environment.GetEnvironmentVariable("ZD_ZAMMAD_TOKEN");
        return o;
    }

    private static string Next(string[] args, ref int i) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {args[i]}");

    public static void PrintUsage()
    {
        Console.WriteLine(
            "Zammad close-date backfill (Zammad-only; emits a reviewable .sql file)\n\n" +
            "Usage:\n" +
            "  zammad-close-date-backfill --zammad-url <url> --zammad-token <token>\n" +
            "                             [--out ./manual-logs/zammad-close-date-backfill.sql] [--page-size 100]\n\n" +
            "Env fallbacks: ZD_ZAMMAD_URL / ZD_ZAMMAD_TOKEN.\n");
    }
}

// ---------------------------------------------------------------------

/// One Zammad ticket that has been closed at least once.
sealed record ClosedTicket(long ZammadId, DateTimeOffset CloseUtc);

static class Backfill
{
    public static async Task<int> RunAsync(Options opts, CancellationToken ct)
    {
        var zammadUrl = opts.RequireZammadUrl().TrimEnd('/');
        var token = opts.RequireZammadToken();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Token token={token}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        Console.WriteLine("1/2  Paging through Zammad tickets to collect close dates…");
        var closed = await CollectClosedTicketsAsync(http, zammadUrl, opts.PageSize, ct);
        Console.WriteLine($"     Found {closed.Count} ticket(s) with a close date in Zammad.");
        if (closed.Count == 0)
        {
            Console.WriteLine("Nothing to backfill. Exiting.");
            return 0;
        }

        Console.WriteLine($"2/2  Writing idempotent SQL to {opts.OutPath} …");
        var sql = BuildSql(closed);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(opts.OutPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(opts.OutPath, sql, new UTF8Encoding(false), ct);

        Console.WriteLine(
            $"\nDone. Review {opts.OutPath}, then apply it where Postgres is reachable, e.g.:\n" +
            $"  psql \"<connstring>\" -1 -f {opts.OutPath}\n" +
            "The script is idempotent and only touches imported tickets that are currently\n" +
            "in a Resolved/Closed status; re-running it never duplicates an event or\n" +
            "overwrites a date that is already set.");
        return 0;
    }

    /// Pages GET /api/v1/tickets and keeps every ticket that carries a
    /// non-null close_at. Stops when a page returns fewer rows than the
    /// page size (the last page). A hard page cap guards against an
    /// endpoint that ignores pagination and loops forever.
    private static async Task<List<ClosedTicket>> CollectClosedTicketsAsync(
        HttpClient http, string zammadUrl, int pageSize, CancellationToken ct)
    {
        const int maxPages = 100_000; // safety backstop, not a real limit
        var result = new List<ClosedTicket>();
        var seen = new HashSet<long>();

        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"{zammadUrl}/api/v1/tickets?page={page}&per_page={pageSize}";
            using var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(
                    "Unexpected Zammad response shape for /api/v1/tickets (expected a JSON array). " +
                    "Check the token's permissions.");

            var count = 0;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                count++;
                if (!t.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                var id = idEl.GetInt64();

                var closeAt = ReadDate(t, "close_at");
                if (closeAt is null) continue;
                if (!seen.Add(id)) continue; // de-dupe across page boundaries
                result.Add(new ClosedTicket(id, closeAt.Value.ToUniversalTime()));
            }

            if (count < pageSize) break; // last page reached
            if (page % 10 == 0) Console.WriteLine($"     …scanned {page} page(s), {result.Count} closed so far");
        }

        result.Sort((a, b) => a.ZammadId.CompareTo(b.ZammadId));
        return result;
    }

    private static DateTimeOffset? ReadDate(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto) ? dto : null;
    }

    private static string BuildSql(IReadOnlyList<ClosedTicket> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- =====================================================================");
        sb.AppendLine("-- Zammad close-date backfill — generated by tools/ZammadCloseDateBackfill");
        sb.AppendLine("--");
        sb.AppendLine("-- Repairs imported tickets so the Resolved / CWI / timesheet lists show");
        sb.AppendLine("-- them under the real Zammad close month instead of the import day.");
        sb.AppendLine("--");
        sb.AppendLine("-- Safe to run more than once: it never duplicates a StatusChange event");
        sb.AppendLine("-- and never overwrites a resolved_utc/closed_utc that is already set.");
        sb.AppendLine("-- It only touches rows that carry a zammad_ticket_id AND are currently");
        sb.AppendLine("-- in a Resolved/Closed status, so app-native tickets are never affected");
        sb.AppendLine("-- and a close date for a never-imported Zammad ticket is simply ignored.");
        sb.AppendLine("-- merged_utc / merged_into_ticket_id are left untouched on purpose");
        sb.AppendLine("-- (they belong to the in-app merge feature).");
        sb.AppendLine($"-- Close dates in this backfill: {rows.Count}");
        sb.AppendLine("-- =====================================================================");
        sb.AppendLine();
        sb.AppendLine("BEGIN;");
        sb.AppendLine();
        sb.AppendLine("CREATE TEMP TABLE _zammad_close (");
        sb.AppendLine("    zammad_id BIGINT PRIMARY KEY,");
        sb.AppendLine("    close_utc TIMESTAMPTZ NOT NULL");
        sb.AppendLine(") ON COMMIT DROP;");
        sb.AppendLine();
        sb.AppendLine("INSERT INTO _zammad_close (zammad_id, close_utc) VALUES");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var ts = r.CloseUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var sep = i == rows.Count - 1 ? ";" : ",";
            sb.AppendLine($"    ({r.ZammadId}, '{ts}+00'){sep}");
        }
        sb.AppendLine();
        sb.AppendLine("-- 1) Synthetic 'status changed to X' event, dated at the real close date.");
        sb.AppendLine("--    Mirrors the shape the app writes for a normal status change so the");
        sb.AppendLine("--    timeline renders it as \"Status set to <name>\". metadata.to drives");
        sb.AppendLine("--    the Resolved/CWI/timesheet month filter. Skipped when a StatusChange");
        sb.AppendLine("--    into the current status already exists (idempotent + respects real ones).");
        sb.AppendLine("INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal, created_utc)");
        sb.AppendLine("SELECT t.id, 'StatusChange', NULL,");
        sb.AppendLine("       jsonb_build_object(");
        sb.AppendLine("           'to', t.status_id::text,");
        sb.AppendLine("           'toName', s.name,");
        sb.AppendLine("           'toCategory', s.state_category,");
        sb.AppendLine("           'source', 'zammad-backfill'),");
        sb.AppendLine("       FALSE, z.close_utc");
        sb.AppendLine("FROM tickets t");
        sb.AppendLine("JOIN _zammad_close z ON z.zammad_id = t.zammad_ticket_id");
        sb.AppendLine("JOIN statuses s ON s.id = t.status_id");
        sb.AppendLine("WHERE t.is_deleted = FALSE");
        sb.AppendLine("  AND s.state_category IN ('Resolved', 'Closed')");
        sb.AppendLine("  AND NOT EXISTS (");
        sb.AppendLine("      SELECT 1 FROM ticket_events e");
        sb.AppendLine("      WHERE e.ticket_id = t.id");
        sb.AppendLine("        AND e.event_type = 'StatusChange'");
        sb.AppendLine("        AND e.metadata->>'to' = t.status_id::text");
        sb.AppendLine("  );");
        sb.AppendLine();
        sb.AppendLine("-- 2) Fill resolved_utc / closed_utc with the same date, only when still NULL.");
        sb.AppendLine("UPDATE tickets t");
        sb.AppendLine("SET resolved_utc = z.close_utc");
        sb.AppendLine("FROM _zammad_close z, statuses s");
        sb.AppendLine("WHERE z.zammad_id = t.zammad_ticket_id");
        sb.AppendLine("  AND s.id = t.status_id");
        sb.AppendLine("  AND s.state_category = 'Resolved'");
        sb.AppendLine("  AND t.resolved_utc IS NULL;");
        sb.AppendLine();
        sb.AppendLine("UPDATE tickets t");
        sb.AppendLine("SET closed_utc = z.close_utc");
        sb.AppendLine("FROM _zammad_close z, statuses s");
        sb.AppendLine("WHERE z.zammad_id = t.zammad_ticket_id");
        sb.AppendLine("  AND s.id = t.status_id");
        sb.AppendLine("  AND s.state_category = 'Closed'");
        sb.AppendLine("  AND t.closed_utc IS NULL;");
        sb.AppendLine();
        sb.AppendLine("COMMIT;");
        sb.AppendLine();
        sb.AppendLine("-- ---------------------------------------------------------------------");
        sb.AppendLine("-- Rollback of the synthetic events (the date columns are left to you):");
        sb.AppendLine("--   DELETE FROM ticket_events");
        sb.AppendLine("--   WHERE event_type = 'StatusChange'");
        sb.AppendLine("--     AND metadata->>'source' = 'zammad-backfill';");
        sb.AppendLine("-- ---------------------------------------------------------------------");
        return sb.ToString();
    }
}
