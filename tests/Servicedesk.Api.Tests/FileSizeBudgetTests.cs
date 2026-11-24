using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// File-size budget for every C# and TypeScript source file in `src/` and
/// `tests/` (CLAUDE.md → Code structure). New files stay at or under
/// <see cref="Limit"/> lines. Files that were already larger when the budget
/// was introduced are listed in `file-size-baseline.txt` with their size at
/// that time: a ratchet — they may shrink, never grow, and they leave the list
/// once they are back under the limit. A handful of files are exempt because
/// their length is one cohesive table or script (<see cref="Exempt"/>).
///
/// After splitting a listed file, run the test once with
/// `SD_FILE_SIZE_BASELINE=tighten` to lower or drop its entry. Tighten mode
/// never raises an entry or adds a new one.
public sealed class FileSizeBudgetTests
{
    public const int Limit = 600;

    private const string BaselineFile = "tests/Servicedesk.Api.Tests/file-size-baseline.txt";

    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["src/Servicedesk.Infrastructure/Persistence/DatabaseBootstrapper.cs"] =
            "the idempotent schema script; its order is the migration order",
        ["src/Servicedesk.Infrastructure/Settings/SettingKeys.cs"] =
            "the settings registry: one entry per key, data only",
        ["src/Servicedesk.Web/src/app/router.tsx"] =
            "the route table",
    };

    private static readonly string[] SkippedDirectories = ["node_modules", "bin", "obj", "dist"];

    [Fact]
    public void Source_files_stay_within_the_line_budget()
    {
        var root = RepoFiles.Root();
        var current = ScanSourceFiles(root);
        var baseline = ReadBaseline(root);

        if (Environment.GetEnvironmentVariable("SD_FILE_SIZE_BASELINE") == "tighten")
        {
            WriteTightenedBaseline(root, baseline, current);
            baseline = ReadBaseline(root);
        }

        var problems = new List<string>();

        foreach (var path in Exempt.Keys.Where(p => !current.ContainsKey(p)))
        {
            problems.Add($"Exempt file no longer exists: {path} — remove it from FileSizeBudgetTests.Exempt.");
        }

        foreach (var (path, recorded) in baseline)
        {
            if (!current.TryGetValue(path, out var lines))
            {
                problems.Add($"Baseline entry for a missing file: {path} — run with SD_FILE_SIZE_BASELINE=tighten.");
            }
            else if (lines > recorded)
            {
                problems.Add($"{path} grew from {recorded} to {lines} lines. Listed files may not grow: "
                    + "move the new code (or an existing part) into its own file.");
            }
            else if (lines < recorded || lines <= Limit)
            {
                problems.Add($"{path} shrank from {recorded} to {lines} lines — run with "
                    + "SD_FILE_SIZE_BASELINE=tighten to lock in the gain.");
            }
        }

        foreach (var (path, lines) in current)
        {
            if (lines > Limit && !baseline.ContainsKey(path) && !Exempt.ContainsKey(path))
            {
                problems.Add($"{path} has {lines} lines (limit {Limit}). Split it by responsibility; "
                    + "new files are never added to the baseline.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static Dictionary<string, int> ScanSourceFiles(string root)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var top in new[] { "src", "tests" })
        {
            Walk(new DirectoryInfo(Path.Combine(root, top)));
        }
        Assert.True(result.Count > 100, "Source scan found almost nothing — has the repository layout changed?");
        return result;

        void Walk(DirectoryInfo dir)
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (!SkippedDirectories.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Walk(sub);
                }
            }
            foreach (var file in dir.EnumerateFiles())
            {
                var name = file.Name;
                var counted = name.EndsWith(".cs", StringComparison.Ordinal)
                    || name.EndsWith(".tsx", StringComparison.Ordinal)
                    || (name.EndsWith(".ts", StringComparison.Ordinal) && !name.EndsWith(".d.ts", StringComparison.Ordinal));
                if (counted)
                {
                    var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    result[relative] = File.ReadLines(file.FullName).Count();
                }
            }
        }
    }

    private static Dictionary<string, int> ReadBaseline(string root)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(Path.Combine(root, BaselineFile)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var space = line.IndexOf(' ');
            Assert.True(space > 0, $"Malformed baseline line: '{raw}' (expected '<lines> <path>').");
            result[line[(space + 1)..].Trim()] = int.Parse(line[..space]);
        }
        return result;
    }

    private static void WriteTightenedBaseline(
        string root, Dictionary<string, int> baseline, Dictionary<string, int> current)
    {
        var path = Path.Combine(root, BaselineFile);
        var header = File.ReadLines(path).TakeWhile(l => l.StartsWith('#')).ToList();
        var entries = baseline
            .Where(e => current.TryGetValue(e.Key, out var lines) && lines > Limit)
            .Select(e => (Path: e.Key, Lines: Math.Min(e.Value, current[e.Key])))
            .OrderByDescending(e => e.Lines)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => $"{e.Lines} {e.Path}");
        File.WriteAllLines(path, header.Concat(entries));
    }
}
