using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Servicedesk.Api.Security;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Search;
using Servicedesk.Infrastructure.Security.IpBlocking;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the IP-blocking safeguards: what auto-blocks (only unambiguous
/// scanners), what can never be blocked (loopback, proxy network, the admin's
/// own address), the known-login confirmation, and that personal data never
/// lands in block evidence.
public sealed class IpBlockingTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static IpThreatConfig Config(int scanner = 2, int rate = 100, string? paths = "/.env*, *credentials*.json, *.php") =>
        new(true, TimeSpan.FromMinutes(10), scanner, rate, 10, 10, TimeSpan.FromHours(24),
            IpThreatConfig.CompileScannerPatterns(paths));

    private static IpBlockList List() =>
        new(new[] { IPNetwork.Parse("172.28.0.0/16") });

    // ---- scanner path patterns ------------------------------------------

    [Theory]
    [InlineData("/.env", true)]
    [InlineData("/.env.production", true)]
    [InlineData("/credentials.json", true)]
    [InlineData("/.config/gcloud/application_default_credentials.json", true)]
    [InlineData("/wp-login.PHP", true)]
    [InlineData("/tickets/123", false)]
    [InlineData("/api/tickets", false)]
    [InlineData("/assets/index-abc.js", false)]
    [InlineData("/environment", false)]
    public void Scanner_patterns_match_only_scanner_paths(string path, bool expected)
    {
        var d = new IpThreatDetector(List());
        d.UpdateConfig(Config());
        Assert.Equal(expected, d.IsScannerPath(path));
    }

    [Fact]
    public void Empty_or_wildcard_only_pattern_list_matches_nothing()
    {
        Assert.Null(IpThreatConfig.CompileScannerPatterns(""));
        Assert.Null(IpThreatConfig.CompileScannerPatterns(" * , "));
    }

    // The shipped default list must parse and behave — a NonBacktracking regex
    // over this list once threw at build time and stopped IP-blocking
    // housekeeping altogether.
    [Theory]
    [InlineData("/.env", true)]
    [InlineData("/app/.env", true)]
    [InlineData("/.git/config", true)]
    [InlineData("/wp-login.php", true)]
    [InlineData("/WP-ADMIN/setup.PHP", true)]
    [InlineData("/cgi-bin/luci", true)]
    [InlineData("/backup-2024.sql", true)]
    [InlineData("/x/application_default_credentials.json", true)]
    [InlineData("/tickets", false)]
    [InlineData("/api/tickets/1/attachments/2", false)]
    [InlineData("/assets/index-abc.js", false)]
    [InlineData("/kb/articles/new", false)]
    [InlineData("/settings/general", false)]
    public void Default_scanner_list_parses_and_matches(string path, bool expected)
    {
        var defaults = Servicedesk.Infrastructure.Settings.SettingDefaults.All
            .Single(d => d.Key == Servicedesk.Infrastructure.Settings.SettingKeys.Security.IpBlockingScannerPaths);
        var matcher = IpThreatConfig.CompileScannerPatterns(defaults.Value);
        Assert.NotNull(matcher);
        Assert.Equal(expected, matcher!.IsMatch(path));
    }

    [Theory]
    [InlineData("/a*b*c", "/aXbYc", true)]
    [InlineData("/a*b*c", "/aXcYb", false)]
    [InlineData("/a*a", "/a", false)]      // prefix and suffix may not overlap
    [InlineData("*.php", ".php", true)]
    [InlineData("/exact", "/EXACT", true)]
    [InlineData("/exact", "/exact/more", false)]
    public void Glob_matching_is_anchored_ordered_and_case_insensitive(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, IpThreatConfig.CompileScannerPatterns(pattern)!.IsMatch(path));
    }

    // ---- detector --------------------------------------------------------

    [Fact]
    public void Threshold_trips_once_per_episode()
    {
        var d = new IpThreatDetector(List());
        d.UpdateConfig(Config(scanner: 2));
        var ip = IPAddress.Parse("35.210.203.170");

        d.Record(ip, IpSignalKind.ScannerPath, "/.env", Now);
        Assert.False(d.Tripped.TryRead(out _));
        d.Record(ip, IpSignalKind.ScannerPath, "/credentials.json", Now.AddSeconds(1));
        Assert.True(d.Tripped.TryRead(out var obs));
        Assert.Equal("35.210.203.170", obs!.Ip);
        Assert.Contains(IpSignalKind.ScannerPath, obs.TrippedKinds);
        Assert.Contains("/.env", obs.SamplePaths);

        d.Record(ip, IpSignalKind.ScannerPath, "/key.json", Now.AddSeconds(2));
        Assert.False(d.Tripped.TryRead(out _));
    }

    [Fact]
    public void Signals_outside_the_window_do_not_count()
    {
        var d = new IpThreatDetector(List());
        d.UpdateConfig(Config(scanner: 2));
        var ip = IPAddress.Parse("203.0.113.9");
        d.Record(ip, IpSignalKind.ScannerPath, "/.env", Now);
        d.Record(ip, IpSignalKind.ScannerPath, "/.env", Now.AddMinutes(11));
        Assert.False(d.Tripped.TryRead(out _));
    }

    [Fact]
    public void Protected_and_whitelisted_addresses_are_never_tracked()
    {
        var list = List();
        list.ApplyRule("198.51.100.7", IpRuleKind.Whitelist, null);
        var d = new IpThreatDetector(list);
        d.UpdateConfig(Config(scanner: 1));

        d.Record(IPAddress.Loopback, IpSignalKind.ScannerPath, "/.env", Now);
        d.Record(IPAddress.Parse("172.28.0.5"), IpSignalKind.ScannerPath, "/.env", Now);
        d.Record(IPAddress.Parse("198.51.100.7"), IpSignalKind.ScannerPath, "/.env", Now);
        d.Record(IPAddress.Parse("::ffff:198.51.100.7"), IpSignalKind.ScannerPath, "/.env", Now);

        Assert.False(d.Tripped.TryRead(out _));
    }

    [Fact]
    public void Disabled_detector_records_nothing()
    {
        var d = new IpThreatDetector(List());
        d.UpdateConfig(IpThreatConfig.Disabled);
        d.Record(IPAddress.Parse("203.0.113.9"), IpSignalKind.ScannerPath, "/.env", Now);
        Assert.False(d.Tripped.TryRead(out _));
        Assert.False(d.IsScannerPath("/.env"));
    }

    // ---- block list ------------------------------------------------------

    [Fact]
    public void Temporary_block_expires_and_permanent_block_does_not()
    {
        var list = List();
        list.ApplyRule("203.0.113.1", IpRuleKind.Block, Now.AddHours(24));
        list.ApplyRule("203.0.113.2", IpRuleKind.Block, null);

        Assert.True(list.IsBlocked("203.0.113.1", Now));
        Assert.False(list.IsBlocked("203.0.113.1", Now.AddHours(25)));
        Assert.True(list.IsBlocked("203.0.113.2", Now.AddYears(5)));
    }

    [Fact]
    public void Protected_networks_can_never_be_blocked_even_from_the_database()
    {
        var list = List();
        list.Replace(new[]
        {
            Rule("127.0.0.1", IpRuleKind.Block),
            Rule("172.28.0.3", IpRuleKind.Block),
        });
        list.ApplyRule("172.28.1.1", IpRuleKind.Block, null);

        Assert.False(list.IsBlocked("127.0.0.1", Now));
        Assert.False(list.IsBlocked("172.28.0.3", Now));
        Assert.False(list.IsBlocked("172.28.1.1", Now));
    }

    [Fact]
    public void Whitelisting_replaces_a_block()
    {
        var list = List();
        list.ApplyRule("203.0.113.1", IpRuleKind.Block, null);
        list.ApplyRule("203.0.113.1", IpRuleKind.Whitelist, null);
        Assert.False(list.IsBlocked("203.0.113.1", Now));
        Assert.True(list.IsWhitelisted("203.0.113.1"));
    }

    [Fact]
    public void Blocked_hits_are_counted_and_drained()
    {
        var list = List();
        list.RecordBlockedHit("203.0.113.1", Now);
        list.RecordBlockedHit("203.0.113.1", Now.AddSeconds(5));
        var drained = list.DrainBlockedHits();
        Assert.Equal(2, drained["203.0.113.1"].Count);
        Assert.Equal(Now.AddSeconds(5), drained["203.0.113.1"].LastUtc);
        Assert.Empty(list.DrainBlockedHits());
    }

    [Fact]
    public void Ipv4_mapped_addresses_normalise_to_ipv4()
    {
        Assert.Equal("1.2.3.4", IpAddressText.Normalize(IPAddress.Parse("::ffff:1.2.3.4")));
        Assert.Equal("1.2.3.4", IpAddressText.TryNormalize(" 1.2.3.4 "));
        Assert.Null(IpAddressText.TryNormalize("1.2.3.0/24"));
        Assert.Null(IpAddressText.TryNormalize("not-an-ip"));
    }

    // ---- audit decorator -------------------------------------------------

    [Fact]
    public async Task Failed_logins_feed_the_detector_without_the_typed_username()
    {
        var d = new IpThreatDetector(List());
        d.UpdateConfig(new IpThreatConfig(true, TimeSpan.FromMinutes(10), 2, 100, 10, 1, TimeSpan.FromHours(24), null));
        var decorator = new IpSignalAuditDecorator(new NullAudit(), d);

        await decorator.LogAsync(new AuditEvent("login_failed", "anon", "anon", "victim@example.com", "203.0.113.9"));

        Assert.True(d.Tripped.TryRead(out var obs));
        Assert.Contains(IpSignalKind.FailedLogin, obs!.TrippedKinds);
        Assert.DoesNotContain("victim@example.com", obs.SamplePaths);
    }

    [Theory]
    [InlineData("rate_limited", true)]
    [InlineData("csrf_rejected", true)]
    [InlineData("portal.login.failed", true)]
    [InlineData("rate_limited_csp_report", false)]
    [InlineData("ticket.attachment.view", false)]
    [InlineData("security.ip.blocked", false)]
    public void Only_abuse_event_types_are_signals(string eventType, bool isSignal)
    {
        Assert.Equal(isSignal, IpSignalAuditDecorator.TryMap(eventType, out _));
    }

    // ---- service safeguards ----------------------------------------------

    [Fact]
    public async Task Scanner_from_an_unknown_ip_is_auto_blocked_for_the_configured_time()
    {
        var (svc, repo, list, _) = Service();
        var (newId, auto) = await svc.HandleObservationAsync(Obs("203.0.113.9", IpSignalKind.ScannerPath), default);

        Assert.True(auto);
        Assert.NotNull(newId);
        Assert.True(list.IsBlocked("203.0.113.9", DateTime.UtcNow));
        Assert.Equal("auto", repo.Rules["203.0.113.9"].Source);
        Assert.NotNull(repo.Rules["203.0.113.9"].ExpiresUtc);
    }

    [Fact]
    public async Task Scanner_hits_from_an_ip_with_successful_logins_are_proposal_only()
    {
        var (svc, repo, list, _) = Service();
        repo.Logins["198.51.100.20"] = 12;
        var (newId, auto) = await svc.HandleObservationAsync(Obs("198.51.100.20", IpSignalKind.ScannerPath), default);

        Assert.False(auto);
        Assert.NotNull(newId);
        Assert.False(list.IsBlocked("198.51.100.20", DateTime.UtcNow));
    }

    [Fact]
    public async Task Non_scanner_signals_never_auto_block()
    {
        var (svc, _, list, _) = Service();
        var (_, auto) = await svc.HandleObservationAsync(Obs("203.0.113.9", IpSignalKind.RateLimited), default);
        Assert.False(auto);
        Assert.False(list.IsBlocked("203.0.113.9", DateTime.UtcNow));
    }

    [Fact]
    public async Task Admin_cannot_block_their_own_address()
    {
        var (svc, _, list, _) = Service();
        var (outcome, _) = await svc.AddRuleAsync("203.0.113.50", IpRuleKind.Block, null, true,
            new IpActor("admin@x", "Admin", "203.0.113.50", null), default);
        Assert.Equal(IpBlockOutcome.OwnIp, outcome);
        Assert.False(list.IsBlocked("203.0.113.50", DateTime.UtcNow));
    }

    [Fact]
    public async Task Proxy_network_cannot_be_blocked_manually()
    {
        var (svc, _, _, _) = Service();
        var (outcome, _) = await svc.AddRuleAsync("172.28.0.2", IpRuleKind.Block, null, true, Admin(), default);
        Assert.Equal(IpBlockOutcome.ProtectedIp, outcome);
    }

    [Fact]
    public async Task Blocking_an_address_with_logins_needs_explicit_confirmation()
    {
        var (svc, repo, list, _) = Service();
        repo.Logins["198.51.100.20"] = 3;

        var (first, known) = await svc.AddRuleAsync("198.51.100.20", IpRuleKind.Block, null, false, Admin(), default);
        Assert.Equal(IpBlockOutcome.KnownLoginsNeedConfirmation, first);
        Assert.Equal(3, known);
        Assert.False(list.IsBlocked("198.51.100.20", DateTime.UtcNow));

        var (second, _) = await svc.AddRuleAsync("198.51.100.20", IpRuleKind.Block, null, true, Admin(), default);
        Assert.Equal(IpBlockOutcome.Ok, second);
        Assert.True(list.IsBlocked("198.51.100.20", DateTime.UtcNow));
    }

    [Fact]
    public async Task Confirming_an_auto_block_makes_it_permanent()
    {
        var (svc, repo, list, _) = Service();
        var (id, _) = await svc.HandleObservationAsync(Obs("203.0.113.9", IpSignalKind.ScannerPath), default);

        var (outcome, _) = await svc.BlockProposalAsync(id!.Value, null, false, Admin(), default);

        Assert.Equal(IpBlockOutcome.Ok, outcome);
        Assert.Null(repo.Rules["203.0.113.9"].ExpiresUtc);
        Assert.Equal("admin", repo.Rules["203.0.113.9"].Source);
        Assert.True(list.IsBlocked("203.0.113.9", DateTime.UtcNow.AddYears(1)));
    }

    [Fact]
    public async Task Dismissing_releases_an_auto_block()
    {
        var (svc, repo, list, _) = Service();
        var (id, _) = await svc.HandleObservationAsync(Obs("203.0.113.9", IpSignalKind.ScannerPath), default);

        Assert.Equal(IpBlockOutcome.Ok, await svc.DismissProposalAsync(id!.Value, Admin(), default));
        Assert.False(list.IsBlocked("203.0.113.9", DateTime.UtcNow));
        Assert.False(repo.Rules.ContainsKey("203.0.113.9"));
        Assert.Equal(IpBlockOutcome.AlreadyDecided, await svc.DismissProposalAsync(id.Value, Admin(), default));
    }

    [Fact]
    public async Task Whitelisting_lifts_the_auto_block_and_stops_new_proposals()
    {
        var (svc, _, list, _) = Service();
        var (id, _) = await svc.HandleObservationAsync(Obs("203.0.113.9", IpSignalKind.ScannerPath), default);
        Assert.Equal(IpBlockOutcome.Ok, await svc.WhitelistProposalAsync(id!.Value, "Pentest partner", Admin(), default));

        Assert.False(list.IsBlocked("203.0.113.9", DateTime.UtcNow));
        var (again, auto) = await svc.HandleObservationAsync(Obs("203.0.113.9", IpSignalKind.ScannerPath), default);
        Assert.Null(again);
        Assert.False(auto);
    }

    [Fact]
    public async Task Decisions_are_audited_with_the_subject_ip_as_target()
    {
        var (svc, _, _, audit) = Service();
        await svc.AddRuleAsync("203.0.113.77", IpRuleKind.Block, "manual", false, Admin(), default);
        var row = Assert.Single(audit.Events, e => e.EventType == "security.ip.blocked");
        Assert.Equal("203.0.113.77", row.Target);
        Assert.Equal("admin@x", row.Actor);
    }

    // ---- health + search -------------------------------------------------

    [Fact]
    public void Open_proposals_turn_the_health_card_to_warning()
    {
        var list = List();
        Assert.Equal(HealthStatus.Ok, HealthAggregator.BuildIpBlocking(list).Status);
        list.SetOpenProposalCount(2);
        var card = HealthAggregator.BuildIpBlocking(list);
        Assert.Equal(HealthStatus.Warning, card.Status);
        Assert.Contains("2 block proposals", card.Summary);
    }

    [Fact]
    public async Task Search_source_is_admin_only_and_agents_get_zero_hits()
    {
        var src = new IpRuleSearchSource(null!);
        var agent = new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>());
        var customer = new SearchPrincipal(Guid.NewGuid(), "Customer", null);
        var admin = new SearchPrincipal(Guid.NewGuid(), "Admin", null);

        Assert.False(src.IsAvailableFor(agent));
        Assert.False(src.IsAvailableFor(customer));
        Assert.True(src.IsAvailableFor(admin));

        // null! data source proves a non-admin never reaches the database.
        var group = await src.SearchAsync(new SearchRequest("203.0", null, 10, 0), agent, default);
        Assert.Empty(group.Hits);
        Assert.Equal(0, group.TotalInGroup);
    }

    // ---- middleware ------------------------------------------------------

    private static (IpBlockMiddleware Mw, bool[] NextCalled) Middleware(IpBlockList list, IpThreatDetector detector, bool disabled = false)
    {
        var called = new[] { false };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:IpBlocking:Disabled"] = disabled ? "true" : "false" })
            .Build();
        var mw = new IpBlockMiddleware(_ => { called[0] = true; return Task.CompletedTask; }, list, detector, config);
        return (mw, called);
    }

    private static DefaultHttpContext Request(string ip, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task Blocked_ip_gets_403_and_never_reaches_the_app()
    {
        var list = List();
        list.ApplyRule("203.0.113.9", IpRuleKind.Block, null);
        var (mw, next) = Middleware(list, new IpThreatDetector(list));
        var ctx = Request("::ffff:203.0.113.9", "/tickets");

        await mw.InvokeAsync(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
        Assert.False(next[0]);
        Assert.Equal(1, list.DrainBlockedHits()["203.0.113.9"].Count);
    }

    [Fact]
    public async Task Scanner_path_is_reported_but_still_served_until_the_block_lands()
    {
        var list = List();
        var detector = new IpThreatDetector(list);
        detector.UpdateConfig(Config(scanner: 1));
        var (mw, next) = Middleware(list, detector);
        var ctx = Request("203.0.113.9", "/credentials.json");

        await mw.InvokeAsync(ctx);

        Assert.True(next[0]);
        Assert.True(detector.Tripped.TryRead(out var obs));
        Assert.Equal("203.0.113.9", obs!.Ip);
    }

    [Fact]
    public async Task Break_glass_setting_lets_everything_through()
    {
        var list = List();
        list.ApplyRule("203.0.113.9", IpRuleKind.Block, null);
        var (mw, next) = Middleware(list, new IpThreatDetector(list), disabled: true);
        var ctx = Request("203.0.113.9", "/tickets");

        await mw.InvokeAsync(ctx);

        Assert.True(next[0]);
        Assert.NotEqual(403, ctx.Response.StatusCode);
    }

    // ---- helpers ---------------------------------------------------------

    private static IpActor Admin() => new("admin@x", "Admin", "198.51.100.1", null);

    private static IpRule Rule(string ip, string kind) =>
        new(ip, kind, "admin", null, null, Now, "x", null, 0, null);

    private static IpThreatObservation Obs(string ip, IpSignalKind kind) =>
        new(ip, new Dictionary<IpSignalKind, int> { [kind] = 3 }, new[] { "/.env" }, new[] { kind }, Now, Now);

    private static (IpBlockService Svc, FakeRepo Repo, IpBlockList List, RecordingAudit Audit) Service()
    {
        var list = List();
        var detector = new IpThreatDetector(list);
        detector.UpdateConfig(Config());
        var repo = new FakeRepo();
        var audit = new RecordingAudit();
        return (new IpBlockService(repo, list, detector, audit), repo, list, audit);
    }

    private sealed class NullAudit : IAuditLogger
    {
        public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();
        public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepo : IIpBlockRepository
    {
        public Dictionary<string, IpRule> Rules { get; } = new();
        public Dictionary<long, IpProposal> Proposals { get; } = new();
        public Dictionary<string, int> Logins { get; } = new();
        private long _nextId = 1;

        public Task<IReadOnlyList<IpRule>> ListActiveRulesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IpRule>>(Rules.Values.ToList());

        public Task<IpRule?> GetRuleAsync(string ip, CancellationToken ct) =>
            Task.FromResult(Rules.TryGetValue(ip, out var r) ? r : null);

        public Task UpsertRuleAsync(string ip, string kind, string source, string? reason, long? proposalId,
            string createdBy, DateTime? expiresUtc, CancellationToken ct)
        {
            Rules[ip] = new IpRule(ip, kind, source, reason, proposalId, DateTime.UtcNow, createdBy, expiresUtc, 0, null);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteRuleAsync(string ip, CancellationToken ct) => Task.FromResult(Rules.Remove(ip));
        public Task<int> DeleteExpiredBlocksAsync(CancellationToken ct) => Task.FromResult(0);
        public Task AddBlockedHitsAsync(IReadOnlyDictionary<string, (long Count, DateTime LastUtc)> hits, CancellationToken ct) => Task.CompletedTask;

        public Task<(long Id, bool Created)> UpsertOpenProposalAsync(IpThreatObservation o, IReadOnlyList<string> reasons,
            string evidenceJson, bool knownLogin, bool autoBlocked, CancellationToken ct)
        {
            var open = Proposals.Values.FirstOrDefault(p => p.Ip == o.Ip && p.Status == IpProposalStatus.Open);
            if (open is not null)
            {
                Proposals[open.Id] = open with { AutoBlocked = open.AutoBlocked || autoBlocked };
                return Task.FromResult((open.Id, false));
            }
            var id = _nextId++;
            Proposals[id] = new IpProposal(id, o.Ip, IpProposalStatus.Open, autoBlocked, reasons, evidenceJson, knownLogin,
                o.FirstSeenUtc, o.LastSeenUtc, DateTime.UtcNow, null, null);
            return Task.FromResult((id, true));
        }

        public Task<IReadOnlyList<IpProposal>> ListProposalsAsync(string? status, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IpProposal>>(Proposals.Values.Where(p => status is null || p.Status == status).ToList());

        public Task<IpProposal?> GetProposalAsync(long id, CancellationToken ct) =>
            Task.FromResult(Proposals.TryGetValue(id, out var p) ? p : null);

        public Task<int> CountOpenProposalsAsync(CancellationToken ct) =>
            Task.FromResult(Proposals.Values.Count(p => p.Status == IpProposalStatus.Open));

        public Task<bool> DecideProposalAsync(long id, string status, string decidedBy, CancellationToken ct)
        {
            if (!Proposals.TryGetValue(id, out var p) || p.Status != IpProposalStatus.Open) return Task.FromResult(false);
            Proposals[id] = p with { Status = status, DecidedBy = decidedBy, DecidedUtc = DateTime.UtcNow };
            return Task.FromResult(true);
        }

        public Task CloseOpenProposalForIpAsync(string ip, string status, string decidedBy, CancellationToken ct)
        {
            foreach (var p in Proposals.Values.Where(p => p.Ip == ip && p.Status == IpProposalStatus.Open).ToList())
                Proposals[p.Id] = p with { Status = status, DecidedBy = decidedBy };
            return Task.CompletedTask;
        }

        public Task<int> CountSuccessfulLoginsAsync(string ip, CancellationToken ct) =>
            Task.FromResult(Logins.TryGetValue(ip, out var n) ? n : 0);
    }
}
