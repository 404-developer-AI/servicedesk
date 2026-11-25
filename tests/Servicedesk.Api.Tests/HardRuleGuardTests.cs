using System.Text.RegularExpressions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Guards for two CLAUDE.md hard rules that otherwise rest on discipline:
/// queue access in SQL goes only through <c>QueueAccessScope</c>, and every
/// search source proves on real SQL that a user without access gets zero
/// hits. A failure names the file or type; the fix is the shared helper or a
/// test — an allow-list entry only for a reviewed, documented exception.
public sealed class HardRuleGuardTests
{
    // ---- queue access only through QueueAccessScope -------------------------

    /// A hand-written queue filter: `queue_id = ANY(…)`, `queue_id IN (…)` or
    /// anything bound to the scope's `@AccessibleQueueIds` parameter. Checked
    /// per line; each allow-list entry covers one line.
    private static readonly Regex QueueFilter = new(
        @"queue_id\s*(=\s*ANY|IN)\s*\(|ANY\s*\(\s*@AccessibleQueueIds",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// Reviewed exceptions: file → (matched text, why). Anything else fails.
    private static readonly Dictionary<string, (string Match, string Why)[]> AllowedQueueFilters = new(StringComparer.Ordinal)
    {
        ["src/Servicedesk.Infrastructure/Access/QueueAccessScope.cs"] =
            [("t.queue_id = ANY(", "the scope itself (TicketQueueVisibleSql)")],
        ["src/Servicedesk.Infrastructure/Insights/InsightsService.cs"] =
            [("t.queue_id = ANY(", "the user's own queue selection, ANDed with the scope predicate")],
        ["src/Servicedesk.Infrastructure/Persistence/Tickets/TicketRepository.Search.cs"] =
            [("t.queue_id = ANY(", "the list's queue filter, ANDed with the scope predicate")],
        ["src/Servicedesk.Infrastructure/Workflow/WorkflowReportService.cs"] =
            [("ANY(@AccessibleQueueIds", "queue id read from a JSON snapshot; standard scope parameter names")],
    };

    [Fact]
    public void Queue_access_filters_are_written_only_by_QueueAccessScope()
    {
        var offenders = new List<string>();
        foreach (var path in RepoFiles.Enumerate("src", "*.cs"))
        {
            var rel = RepoFiles.Relative(path);
            var allowed = AllowedQueueFilters.GetValueOrDefault(rel) ?? [];
            var budget = allowed.GroupBy(a => a.Match).ToDictionary(g => g.Key, g => g.Count());
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!QueueFilter.IsMatch(lines[i])) continue;
                var hit = budget.Keys.FirstOrDefault(k => lines[i].Contains(k, StringComparison.OrdinalIgnoreCase));
                if (hit is not null && budget[hit] > 0) { budget[hit]--; continue; }
                offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Filter queue access through QueueAccessScope (SqlPredicate / TicketQueueVisibleSql + AddTo), "
            + "not by hand (CLAUDE.md → Security rules):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Only_the_access_layer_reads_the_queue_grant_table()
    {
        var offenders = RepoFiles.Enumerate("src", "*.cs")
            .Select(RepoFiles.Relative)
            .Where(rel => !rel.StartsWith("src/Servicedesk.Infrastructure/Access/", StringComparison.Ordinal)
                          && rel != "src/Servicedesk.Infrastructure/Persistence/DatabaseBootstrapper.cs")
            .Where(rel => File.ReadAllText(Path.Combine(RepoFiles.Root(), rel))
                .Contains("user_queue_access", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Read queue grants through IQueueAccessService / QueueAccessScope: " + string.Join(", ", offenders));
    }

    // ---- every search source has a real-SQL zero-hit test -------------------

    /// Sources without their own real-SQL test, and why.
    private static readonly Dictionary<string, string> SearchSourcesWithoutDbTest = new(StringComparer.Ordinal)
    {
        ["ScopedSearchSource"] = "the wrapper that adds the role/flag gate; covered by its own unit tests",
        ["SettingsSearchSource"] = "searches the in-memory settings registry, no SQL; PerfSearchTests",
    };

    [Fact]
    public void Every_search_source_is_named_in_a_real_database_test()
    {
        var sources = typeof(Servicedesk.Infrastructure.Search.TicketSearchSource).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISearchSource).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(sources.Count > 20, "Found almost no ISearchSource implementations — has the assembly moved?");

        var dbTests = string.Join("\n", RepoFiles.Enumerate("tests/Servicedesk.Api.Tests/Database", "*.cs").Select(File.ReadAllText));
        var missing = sources
            .Where(name => !SearchSourcesWithoutDbTest.ContainsKey(name))
            .Where(name => !Regex.IsMatch(dbTests, $@"\b{Regex.Escape(name)}\b"))
            .OrderBy(n => n)
            .ToList();

        Assert.True(missing.Count == 0,
            "Every ISearchSource needs a real-database test proving a user without access gets zero hits "
            + "(CLAUDE.md → Global search; tests/Servicedesk.Api.Tests/Database): " + string.Join(", ", missing));
        Assert.All(SearchSourcesWithoutDbTest.Keys, name => Assert.Contains(name, sources));
    }

    // ---- bulk mail only through Campaigns, through the one mail path --------

    [Fact]
    public void Campaigns_send_only_through_the_single_ticket_mail_service()
    {
        // No second mail path: campaign code never talks to Graph directly.
        var offenders = RepoFiles.Enumerate("src/Servicedesk.Infrastructure/Campaigns", "*.cs")
            .Concat(RepoFiles.Enumerate("src/Servicedesk.Api/Campaigns", "*.cs"))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\bIGraphMailClient\b|\bGraphMailClient\b"))
            .Select(RepoFiles.Relative)
            .ToList();
        Assert.True(offenders.Count == 0,
            "Campaign mail goes through IOutboundMailService only (CLAUDE.md → Campaigns): " + string.Join(", ", offenders));

        var sender = File.ReadAllText(Path.Combine(RepoFiles.Root(), "src/Servicedesk.Infrastructure/Campaigns/CampaignSendService.cs"));
        Assert.Contains("IOutboundMailService", sender);
    }

    [Fact]
    public void The_generic_bulk_action_never_sends_mail()
    {
        var files = new[]
        {
            "src/Servicedesk.Infrastructure/Tickets/TicketBulkActionService.cs",
            "src/Servicedesk.Web/src/pages/tickets/components/BulkActionDialog.tsx",
        };
        var mail = new Regex(@"IOutboundMailService|IGraphMailClient|SendMailAsync|/mail[""'`]", RegexOptions.Compiled);
        var offenders = files.Where(f => mail.IsMatch(File.ReadAllText(Path.Combine(RepoFiles.Root(), f)))).ToList();
        Assert.True(offenders.Count == 0,
            "Bulk actions post no outbound mail; bulk mail exists only as Campaigns (CLAUDE.md → Bulk actions): "
            + string.Join(", ", offenders));
    }
}
