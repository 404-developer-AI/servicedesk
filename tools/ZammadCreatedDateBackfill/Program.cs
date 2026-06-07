using System.Globalization;
using System.Text;
using System.Text.Json;

// =====================================================================
// Zammad created-date backfill — one-time repair for imported tickets.
//
// Why this exists
// ---------------
// The Zammad import never carried the upstream creation date onto the
// local ticket row: imported rows got `tickets.created_utc = now()` (the
// import day) instead of the date the ticket was originally opened in
// Zammad. Every imported ticket therefore shows the import day as its
// "created" date in the list, the side panel, and any date filter.
//
// (The timeline already carries the real dates — the 'Created' event and
// each article keep their upstream created_at — so only the ticket row's
// own created_utc column is wrong. This tool fixes that column using the
// authoritative ticket-level created_at straight from Zammad.)
//
// What this tool does
// -------------------
// 1. Talks ONLY to Zammad (no database connection needed). It pages
//    through every ticket and collects each one's real creation date
//    (`created_at`).
// 2. Emits an idempotent, reviewable .sql file that, when YOU run it
//    against the Servicedesk database, sets `tickets.created_utc` to the
//    real Zammad creation date — but ONLY when the upstream date is
//    EARLIER than the value currently stored. That single guard makes the
//    script:
//      - safe for app-native tickets — they carry no zammad_ticket_id, so
//        the join never matches them;
//      - safe to re-run — once a row has been corrected its stored date
//        already equals the upstream date, so the guard skips it;
//      - non-destructive of any already-correct date — it only ever moves
//        a date backwards from the import day to the real (earlier) date.
//
// The tool itself NEVER writes to any database. It only produces SQL for
// you to inspect and apply where Postgres is reachable. created_utc is
// overwritten in place, so take a database backup before applying (there
// is no automatic rollback for an overwritten column).
//
// Usage
//   zammad-created-date-backfill \
//       --zammad-url   https://support.example.com \
//       --zammad-token <api-token> \
//       [--out ./manual-logs/zammad-created-date-backfill.sql] \
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
    public string OutPath { get; private set; } = Path.Combine("manual-logs", "zammad-created-date-backfill.sql");
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
            "Zammad created-date backfill (Zammad-only; emits a reviewable .sql file)\n\n" +
            "Usage:\n" +
            "  zammad-created-date-backfill --zammad-url <url> --zammad-token <token>\n" +
            "                               [--out ./manual-logs/zammad-created-date-backfill.sql] [--page-size 100]\n\n" +
            "Env fallbacks: ZD_ZAMMAD_URL / ZD_ZAMMAD_TOKEN.\n");
    }
}

// ---------------------------------------------------------------------

/// One Zammad ticket with its upstream creation date.
sealed record CreatedTicket(long ZammadId, DateTimeOffset CreatedUtc);

static class Backfill
{
    public static async Task<int> RunAsync(Options opts, CancellationToken ct)
    {
        var zammadUrl = opts.RequireZammadUrl().TrimEnd('/');
        var token = opts.RequireZammadToken();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Token token={token}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        Console.WriteLine("1/2  Paging through Zammad tickets to collect creation dates…");
        var created = await CollectCreatedTicketsAsync(http, zammadUrl, opts.PageSize, ct);
        Console.WriteLine($"     Found {created.Count} ticket(s) with a creation date in Zammad.");
        if (created.Count == 0)
        {
            Console.WriteLine("Nothing to backfill. Exiting.");
            return 0;
        }

        Console.WriteLine($"2/2  Writing idempotent SQL to {opts.OutPath} …");
        var sql = BuildSql(created);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(opts.OutPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(opts.OutPath, sql, new UTF8Encoding(false), ct);

        Console.WriteLine(
            $"\nDone. Review {opts.OutPath}, then apply it where Postgres is reachable, e.g.:\n" +
            $"  psql \"<connstring>\" -1 -f {opts.OutPath}\n" +
            "The script is idempotent and only touches imported tickets (those carrying a\n" +
            "zammad_ticket_id); it only moves created_utc backwards to the real Zammad date,\n" +
            "so app-native tickets and already-corrected rows are never changed. Take a\n" +
            "database backup first — created_utc is overwritten in place.");
        return 0;
    }

    /// Pages GET /api/v1/tickets and keeps every ticket that carries a
    /// non-null created_at (all of them, in practice). Stops when a page
    /// returns fewer rows than the page size (the last page). A hard page
    /// cap guards against an endpoint that ignores pagination and loops
    /// forever.
    private static async Task<List<CreatedTicket>> CollectCreatedTicketsAsync(
        HttpClient http, string zammadUrl, int pageSize, CancellationToken ct)
    {
        const int maxPages = 100_000; // safety backstop, not a real limit
        var result = new List<CreatedTicket>();
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

                var createdAt = ReadDate(t, "created_at");
                if (createdAt is null) continue;
                if (!seen.Add(id)) continue; // de-dupe across page boundaries
                result.Add(new CreatedTicket(id, createdAt.Value.ToUniversalTime()));
            }

            if (count < pageSize) break; // last page reached
            if (page % 10 == 0) Console.WriteLine($"     …scanned {page} page(s), {result.Count} collected so far");
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

    private static string BuildSql(IReadOnlyList<CreatedTicket> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- =====================================================================");
        sb.AppendLine("-- Zammad created-date backfill — generated by tools/ZammadCreatedDateBackfill");
        sb.AppendLine("--");
        sb.AppendLine("-- Repairs imported tickets whose created_utc was stamped with the import");
        sb.AppendLine("-- day instead of the date the ticket was originally opened in Zammad.");
        sb.AppendLine("--");
        sb.AppendLine("-- Safe to run more than once: it only touches rows that carry a");
        sb.AppendLine("-- zammad_ticket_id AND whose stored created_utc is LATER than the real");
        sb.AppendLine("-- Zammad date, so app-native tickets are never affected and an");
        sb.AppendLine("-- already-corrected row is skipped on a second run. It only ever moves a");
        sb.AppendLine("-- created date backwards (import day -> real, earlier date).");
        sb.AppendLine("--");
        sb.AppendLine("-- NOTE: created_utc is overwritten in place; there is no automatic");
        sb.AppendLine("-- rollback. Take a database backup before applying.");
        sb.AppendLine($"-- Creation dates in this backfill: {rows.Count}");
        sb.AppendLine("-- =====================================================================");
        sb.AppendLine();
        sb.AppendLine("BEGIN;");
        sb.AppendLine();
        sb.AppendLine("CREATE TEMP TABLE _zammad_created (");
        sb.AppendLine("    zammad_id   BIGINT PRIMARY KEY,");
        sb.AppendLine("    created_utc TIMESTAMPTZ NOT NULL");
        sb.AppendLine(") ON COMMIT DROP;");
        sb.AppendLine();
        sb.AppendLine("INSERT INTO _zammad_created (zammad_id, created_utc) VALUES");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var ts = r.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var sep = i == rows.Count - 1 ? ";" : ",";
            sb.AppendLine($"    ({r.ZammadId}, '{ts}+00'){sep}");
        }
        sb.AppendLine();
        sb.AppendLine("-- Move created_utc back to the real Zammad date, only when the stored");
        sb.AppendLine("-- value is later (i.e. the import day). The join on zammad_ticket_id");
        sb.AppendLine("-- restricts this to imported tickets; the comparison makes it idempotent.");
        sb.AppendLine("UPDATE tickets t");
        sb.AppendLine("SET created_utc = z.created_utc");
        sb.AppendLine("FROM _zammad_created z");
        sb.AppendLine("WHERE z.zammad_id = t.zammad_ticket_id");
        sb.AppendLine("  AND t.created_utc > z.created_utc;");
        sb.AppendLine();
        sb.AppendLine("COMMIT;");
        return sb.ToString();
    }
}
