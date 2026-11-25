using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Auth.Totp;
using Servicedesk.Infrastructure.Dashboard;
using Servicedesk.Infrastructure.DataProtection;
using Servicedesk.Infrastructure.Persistence;
using Servicedesk.Infrastructure.Reporting;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// Boots the real Servicedesk host but replaces Postgres-bound services with
/// in-memory fakes so tests can run without a database. The rate limiter, CSP,
/// security headers and all middleware are real — those are what we test.
public sealed class SecurityBaselineFactory : WebApplicationFactory<Program>
{
    public readonly FakeAuditLogger Audit = new();
    public readonly FakeAuditQuery AuditQuery = new();
    public readonly FakeUserService Users = new();
    public readonly FakeSessionService Sessions = new();
    public readonly FakeTotpService Totp = new();
    public readonly InMemorySettingsService Settings = new();
    public readonly InMemorySecretStore Secrets = new();

    // Service providers of every host built from this factory (derived
    // WithWebHostBuilder hosts included), so ResetState can reach the fakes
    // that are registered by type and therefore live per host.
    private readonly ConcurrentBag<IServiceProvider> _builtHosts = new();

    private readonly Dictionary<string, string?> _overrides = new()
    {
        ["ConnectionStrings:Postgres"] = "Host=localhost;Database=servicedesk_test_stub;Username=stub;Password=stub",
        ["Audit:HashKey"] = "dGVzdC1rZXktZm9yLWNpLW9ubHktbm90LXNlY3JldA==",
        // 32 zero bytes, base64. Real key lives in env; tests never touch the DB
        // keyring — we swap PostgresXmlRepository out for an in-memory one below.
        ["DataProtection:MasterKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        ["Security:RateLimit:Global:PermitPerWindow"] = "1000",
        ["Security:RateLimit:Global:WindowSeconds"] = "60",
    };

    /// Puts every shared fake back in its initial state, so one host can serve
    /// a whole test class (SharedHostFixture). Fakes registered by type live
    /// per host; <see cref="SharedHostFixture"/> resets those on its hosts.
    public void ResetState()
    {
        Audit.Clear();
        AuditQuery.Clear();
        Users.Reset();
        Sessions.Reset();
        Totp.Reset();
        Settings.Reset();
        Secrets.Clear();
        foreach (var services in _builtHosts)
        {
            (services.GetService<Servicedesk.Infrastructure.Portal.IPortalAccountRepository>() as FakePortalAccountRepository)?.Reset();
        }
    }

    public SecurityBaselineFactory WithConfig(string key, string? value)
    {
        _overrides[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(_overrides);
        });

        builder.ConfigureServices(services =>
        {
            // Must stay: without it every disposed host stays in memory
            // (see the filter; guarded by TestHostLeakTests).
            services.AddTransient<IStartupFilter, NoExecutionContextFlowStartupFilter>();
            services.AddSingleton<IStartupFilter>(new TrackBuiltHostStartupFilter(_builtHosts));

            // Strip DB-backed hosted services.
            services.RemoveAll<IHostedService>();
            // Keep the secret validator (it only reads config).
            services.AddHostedService<Servicedesk.Infrastructure.Secrets.StartupSecretValidator>();

            // Remove NpgsqlDataSource so nothing tries to connect.
            var dsDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(NpgsqlDataSource));
            if (dsDescriptor is not null) services.Remove(dsDescriptor);

            // Replace audit + settings services with fakes.
            services.RemoveAll<IAuditLogger>();
            services.AddSingleton<IAuditLogger>(Audit);

            services.RemoveAll<IAuditQuery>();
            services.AddSingleton<IAuditQuery>(AuditQuery);

            services.RemoveAll<ISettingsService>();
            services.AddSingleton<ISettingsService>(Settings);

            // ProtectedSecretStore is Npgsql-backed; swap an in-memory one so
            // secret-gated surfaces are testable without a database.
            services.RemoveAll<IProtectedSecretStore>();
            services.AddSingleton<IProtectedSecretStore>(Secrets);

            // Timesheet import service is Npgsql-backed and is injected as a
            // handler parameter on the import endpoints, so it must resolve
            // even for requests the auth gate rejects. No-op fake.
            services.RemoveAll<ITimesheetImportService>();
            services.AddSingleton<ITimesheetImportService, FakeTimesheetImportService>();

            // Ticket report service (Reporting API) is Npgsql-backed and is
            // injected as a handler parameter on the public reporting
            // endpoint, so it must resolve even for requests the auth gate
            // rejects. Empty fake.
            services.RemoveAll<ITicketReportService>();
            services.AddSingleton<ITicketReportService, FakeTicketReportService>();

            // Auth services: swap out the DB-backed impls for in-memory fakes.
            // Tests that exercise login/session flow poke these directly; tests
            // that only verify middleware/authorization pass an empty state.
            services.RemoveAll<IUserService>();
            services.AddSingleton<IUserService>(Users);
            services.RemoveAll<ISessionService>();
            services.AddSingleton<ISessionService>(Sessions);
            services.RemoveAll<ITotpService>();
            services.AddSingleton<ITotpService>(Totp);

            // v0.1.0 — customer-portal handlers inject Npgsql-backed portal
            // singletons; swap no-op doubles so authorization / CSRF / disabled
            // behaviour is testable without a database.
            services.RemoveAll<Servicedesk.Infrastructure.Portal.IPortalAccountRepository>();
            services.AddSingleton<Servicedesk.Infrastructure.Portal.IPortalAccountRepository, FakePortalAccountRepository>();
            services.RemoveAll<Servicedesk.Infrastructure.Portal.IPortalTicketRepository>();
            services.AddSingleton<Servicedesk.Infrastructure.Portal.IPortalTicketRepository, FakePortalTicketRepository>();
            services.RemoveAll<Servicedesk.Infrastructure.Portal.IPortalAccountService>();
            services.AddSingleton<Servicedesk.Infrastructure.Portal.IPortalAccountService, FakePortalAccountService>();

            // /auth/me reads per-user dashboard tile preferences — swap the
            // Postgres-backed service for a no-op so DI does not pull in
            // NpgsqlDataSource (removed above).
            services.RemoveAll<IDashboardTilesService>();
            services.AddSingleton<IDashboardTilesService, FakeDashboardTilesService>();

            // Swap the Postgres-backed Data Protection keyring for an in-memory
            // one so tests never reach (and never need) the stubbed datasource.
            services.RemoveAll<PostgresXmlRepository>();
            services.RemoveAll<IConfigureOptions<KeyManagementOptions>>();
            var inMemoryRepo = new InMemoryXmlRepository();
            services.Configure<KeyManagementOptions>(o => o.XmlRepository = inMemoryRepo);
        });
    }
}

/// Builds the request pipeline with execution-context flow suppressed, so
/// timers the middleware create don't capture the test host.
///
/// Mvc.Testing runs Program on its own thread and keeps its HostingListener
/// (which references the host) in an AsyncLocal there. Every Timer captures
/// the current execution context, and the rate limiter's replenishment timers,
/// created when the pipeline is built, live for the rest of the process
/// (RateLimitingMiddleware is not disposable). Together with the middleware's
/// own next-delegate (cut by UseServicedeskRateLimiter) that pinned every
/// disposed host in memory (~15 MB each, hundreds per run): the CI runner ran
/// low on memory and the backend job slowed from ~15 minutes to an hour.
/// TestHostLeakTests guards both.
///
/// The filter runs on that thread right before the pipeline is built, inside
/// the host's async StartAsync; the suppression ends when that method yields,
/// so it covers the middleware constructors. Test-only: production has no such
/// AsyncLocal at startup.
internal sealed class NoExecutionContextFlowStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (!ExecutionContext.IsFlowSuppressed())
        {
            ExecutionContext.SuppressFlow();
        }
        next(app);
    };
}

/// Records each host's service provider when its pipeline is built.
internal sealed class TrackBuiltHostStartupFilter(ConcurrentBag<IServiceProvider> builtHosts) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        builtHosts.Add(app.ApplicationServices);
        next(app);
    };
}

internal sealed class InMemoryXmlRepository : IXmlRepository
{
    private readonly List<XElement> _elements = new();
    private readonly object _lock = new();

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_lock) { return _elements.ToArray(); }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (_lock) { _elements.Add(element); }
    }
}

/// No-op <see cref="ITimesheetImportService"/> so the import endpoints'
/// handler parameters resolve without a database. The auth-gate tests never
/// reach these methods; the happy-path test only asserts the empty shape.
public sealed class FakeTimesheetImportService : ITimesheetImportService
{
    public Task<IReadOnlyList<TimesheetTask>> ListTasksAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TimesheetTask>>(Array.Empty<TimesheetTask>());

    public Task<IReadOnlyList<TimesheetImportUser>> ListUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TimesheetImportUser>>(Array.Empty<TimesheetImportUser>());

    public Task<TimesheetImportBatchResult> ImportBatchAsync(
        string importSource, IReadOnlyList<TimesheetImportRow> rows, CancellationToken ct = default) =>
        Task.FromResult(new TimesheetImportBatchResult(
            rows.Count, rows.Count, 0, Array.Empty<TimesheetImportSkip>()));
}

/// Empty <see cref="ITicketReportService"/> so the Reporting API endpoint's
/// handler parameters resolve without a database. The auth-gate tests never
/// reach it; the happy-path test only asserts the empty shape.
public sealed class FakeTicketReportService : ITicketReportService
{
    public Task<TicketPeriodReport> GetPeriodReportAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxItems,
        int openedOffset, int closedOffset, int openOffset, Guid? companyId,
        CancellationToken ct = default)
    {
        var empty = new TicketReportSection(0, Array.Empty<TicketReportItem>(), 0, false);
        return Task.FromResult(new TicketPeriodReport(empty, empty, empty));
    }

    public Task<CompanyReportList> ListCompaniesAsync(
        int maxItems, int offset, CancellationToken ct = default) =>
        Task.FromResult(new CompanyReportList(0, Array.Empty<CompanyReportItem>(), 0, false));
}

public sealed class FakeSessionService : ISessionService
{
    public sealed record Entry(Guid Id, Guid UserId, string Amr, DateTime ExpiresUtc, DateTime LastSeenUtc, bool Revoked, Guid? ImpersonatorUserId = null, Guid PublicId = default);

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    /// v0.1.0 — per-user role override for the synthesised principal
    /// (default Admin). Portal tests register a Customer user here.
    public ConcurrentDictionary<Guid, string> Roles { get; } = new();

    public void Reset()
    {
        _sessions.Clear();
        Roles.Clear();
    }

    public Task<Guid> CreateAsync(Guid userId, string? ip, string? userAgent, TimeSpan lifetime, string amr, Guid? impersonatorUserId = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        _sessions[id] = new Entry(id, userId, amr, DateTime.UtcNow.Add(lifetime), DateTime.UtcNow, false, impersonatorUserId, Guid.NewGuid());
        return Task.FromResult(id);
    }

    public Task<SessionValidation?> ValidateAsync(Guid sessionId, TimeSpan idleTimeout, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var e) || e.Revoked || e.ExpiresUtc <= DateTime.UtcNow)
        {
            return Task.FromResult<SessionValidation?>(null);
        }
        // Tests supply the user via FakeUserService-resolved principal, but the
        // handler in production only needs a user. We synthesise a minimal one.
        var role = Roles.TryGetValue(e.UserId, out var r) ? r : "Admin";
        var user = new ApplicationUser(e.UserId, "test@example.com", "", role, DateTime.UtcNow, null, 0, null,
            AuthModes.Local, null, null, true);
        return Task.FromResult<SessionValidation?>(new SessionValidation(e.Id, user, e.Amr, e.ExpiresUtc, e.ImpersonatorUserId));
    }

    public Task TouchAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var e))
        {
            _sessions[sessionId] = e with { LastSeenUtc = DateTime.UtcNow };
        }
        return Task.CompletedTask;
    }

    /// Test helper: pretend the session was last active `by` ago (an agent who
    /// has been working a while but is still inside the idle window).
    public void Backdate(Guid sessionId, TimeSpan by)
    {
        if (_sessions.TryGetValue(sessionId, out var e))
        {
            _sessions[sessionId] = e with { LastSeenUtc = DateTime.UtcNow - by };
        }
    }

    /// Test helper: read the session's current last-seen stamp.
    public DateTime? LastSeen(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var e) ? e.LastSeenUtc : null;

    public Task RevokeAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var e))
        {
            _sessions[sessionId] = e with { Revoked = true };
        }
        return Task.CompletedTask;
    }

    public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        foreach (var kv in _sessions)
        {
            if (kv.Value.UserId == userId && !kv.Value.Revoked)
            {
                _sessions[kv.Key] = kv.Value with { Revoked = true };
            }
        }
        return Task.CompletedTask;
    }

    public Task RevokeAllForUserExceptAsync(Guid userId, Guid keepSessionId, CancellationToken ct = default)
    {
        foreach (var kv in _sessions)
        {
            if (kv.Value.UserId == userId && kv.Key != keepSessionId && !kv.Value.Revoked)
            {
                _sessions[kv.Key] = kv.Value with { Revoked = true };
            }
        }
        return Task.CompletedTask;
    }

    public Task UpgradeAmrAsync(Guid sessionId, string amr, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var e))
        {
            _sessions[sessionId] = e with { Amr = amr };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActiveSessionRow>> ListActiveForUserAsync(Guid userId, TimeSpan idleTimeout, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        IReadOnlyList<ActiveSessionRow> rows = _sessions.Values
            .Where(e => e.UserId == userId && !e.Revoked && e.ImpersonatorUserId is null && e.ExpiresUtc > now
                        && (idleTimeout <= TimeSpan.Zero || now - e.LastSeenUtc <= idleTimeout))
            .OrderByDescending(e => e.LastSeenUtc)
            .Select(e => new ActiveSessionRow(e.Id, e.PublicId, e.LastSeenUtc, e.LastSeenUtc, e.ExpiresUtc, "127.0.0.1", "test-agent", e.Amr))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<Guid?> RevokeByPublicIdForUserAsync(Guid userId, Guid publicId, CancellationToken ct = default)
    {
        foreach (var kv in _sessions)
        {
            if (kv.Value.PublicId == publicId && kv.Value.UserId == userId && !kv.Value.Revoked)
            {
                _sessions[kv.Key] = kv.Value with { Revoked = true };
                return Task.FromResult<Guid?>(kv.Key);
            }
        }
        return Task.FromResult<Guid?>(null);
    }

    /// Test helper: the public handle of a session (what "My sessions" shows).
    public Guid PublicIdOf(Guid sessionId) => _sessions[sessionId].PublicId;

    /// Test helper: whether a session has been revoked.
    public bool IsRevoked(Guid sessionId) => _sessions.TryGetValue(sessionId, out var e) && e.Revoked;
}

public sealed class FakeDashboardTilesService : IDashboardTilesService
{
    public Task<IReadOnlyList<DashboardTilePref>> GetForUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DashboardTilePref>>(Array.Empty<DashboardTilePref>());

    public Task<SetDashboardTilesResult> SetEnabledForUserAsync(
        Guid userId,
        IReadOnlyDictionary<string, string> tileSizes,
        Guid actingAdminId,
        CancellationToken ct = default) =>
        Task.FromResult<SetDashboardTilesResult>(new SetDashboardTilesResult.Updated(
            tileSizes.Select(kv => new DashboardTilePref(kv.Key, kv.Value)).ToList()));

    public Task<SetDashboardTilesResult> SaveLayoutForUserAsync(
        Guid userId,
        IReadOnlyList<DashboardTilePref> layout,
        CancellationToken ct = default) =>
        Task.FromResult<SetDashboardTilesResult>(new SetDashboardTilesResult.Updated(layout));
}

public sealed class FakeTotpService : ITotpService
{
    public bool Enabled { get; set; }

    /// Test seam (v0.1.3): what VerifyAsync answers. Default Rejected so the
    /// legacy tests keep their behaviour; step-up tests flip it to accepted.
    public TwoFactorResult VerifyResult { get; set; } = TwoFactorResult.Rejected;

    public void Reset()
    {
        Enabled = false;
        VerifyResult = TwoFactorResult.Rejected;
    }

    public Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Enabled);
    public Task<TotpEnrollment> BeginEnrollAsync(Guid userId, string accountLabel, CancellationToken ct = default) =>
        Task.FromResult(new TotpEnrollment("JBSWY3DPEHPK3PXP", "otpauth://totp/Servicedesk:test?secret=JBSWY3DPEHPK3PXP"));
    public Task<IReadOnlyList<string>?> ConfirmEnrollAsync(Guid userId, string code, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>?>(new[] { "aaaa-bbbb-cccc-dddd" });
    public Task<TwoFactorResult> VerifyAsync(Guid userId, string code, CancellationToken ct = default) =>
        Task.FromResult(VerifyResult);
    public Task DisableAsync(Guid userId, CancellationToken ct = default)
    {
        Enabled = false;
        return Task.CompletedTask;
    }
}
