using System.Text.Json;
using TimesheetMigrator;

// =====================================================================
// Timesheet migrator — one-time bridge from the legacy MSSQL TimeSheet
// database into a target Servicedesk install's migration import surface.
//
//   timesheet-migrator map     [options]   → write a best-guess mapping.json
//   timesheet-migrator import  [options]   → transform + push rows
//
// Options (or matching TS_* environment variables):
//   --source <connstring>   TS_SOURCE_SQL    MSSQL connection string
//   --target <baseUrl>      TS_TARGET_URL    Servicedesk base URL (dev or prod)
//   --token  <secret>       TS_TARGET_TOKEN  import token from Settings panel
//   --source-tz <ianaId>    TS_SOURCE_TZ     time zone of the source's UTC
//                                            start/end times (default Europe/Brussels)
//   --mapping <path>        (default ./mapping.json)
//   --batch <n>             (import only, default 1000)
//   --dry-run               (import only, transform + report, do not POST)
//
// Workflow: run `map`, hand-edit mapping.json to fill unmatched rows, then
// run `import --dry-run` against dev, verify, then `import` for real, then
// repeat against prod with the same mapping.json.
// =====================================================================

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var (command, opts) = Args.Parse(args);
    return command switch
    {
        "map" => await Commands.MapAsync(opts, cts.Token),
        "import" => await Commands.ImportAsync(opts, cts.Token),
        _ => Usage(),
    };
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

static int Usage()
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  timesheet-migrator map     --source <connstr> --target <url> --token <secret> [--mapping mapping.json]\n" +
        "  timesheet-migrator import  --source <connstr> --target <url> --token <secret> [--mapping mapping.json] [--batch 1000] [--dry-run]\n" +
        "\nValues also read from TS_SOURCE_SQL / TS_TARGET_URL / TS_TARGET_TOKEN.");
    return 64;
}

// ---------------------------------------------------------------------

static class Args
{
    public static (string Command, Options Opts) Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "";
        var o = new Options();
        for (var i = command.Length > 0 ? 1 : 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source": o.Source = Next(args, ref i); break;
                case "--target": o.Target = Next(args, ref i); break;
                case "--token": o.Token = Next(args, ref i); break;
                case "--source-tz": o.SourceTimeZone = Next(args, ref i); break;
                case "--mapping": o.MappingPath = Next(args, ref i); break;
                case "--batch": o.BatchSize = int.Parse(Next(args, ref i)); break;
                case "--dry-run": o.DryRun = true; break;
            }
        }
        o.Source ??= Environment.GetEnvironmentVariable("TS_SOURCE_SQL");
        o.Target ??= Environment.GetEnvironmentVariable("TS_TARGET_URL");
        o.Token ??= Environment.GetEnvironmentVariable("TS_TARGET_TOKEN");
        o.SourceTimeZone ??= Environment.GetEnvironmentVariable("TS_SOURCE_TZ");
        return (command, o);
    }

    private static string Next(string[] args, ref int i) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {args[i]}");
}

sealed class Options
{
    public string? Source { get; set; }
    public string? Target { get; set; }
    public string? Token { get; set; }
    public string? SourceTimeZone { get; set; }
    public string MappingPath { get; set; } = "mapping.json";
    public int BatchSize { get; set; } = 1000;
    public bool DryRun { get; set; }

    /// The source stores start/end as UTC wall-clock in offset-less
    /// datetime2 columns; this is the zone its legacy app rendered them in.
    public string SourceTimeZoneId => SourceTimeZone ?? "Europe/Brussels";

    public string RequireSource() => Source ?? throw new ArgumentException("Missing --source / TS_SOURCE_SQL.");
    public string RequireTarget() => Target ?? throw new ArgumentException("Missing --target / TS_TARGET_URL.");
    public string RequireToken() => Token ?? throw new ArgumentException("Missing --token / TS_TARGET_TOKEN.");
}
