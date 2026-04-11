using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
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
using Servicedesk.Infrastructure.DataProtection;
using Servicedesk.Infrastructure.Persistence;
using Servicedesk.Infrastructure.Settings;

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

            // Auth services: swap out the DB-backed impls for in-memory fakes.
            // Tests that exercise login/session flow poke these directly; tests
            // that only verify middleware/authorization pass an empty state.
            services.RemoveAll<IUserService>();
            services.AddSingleton<IUserService>(Users);
            services.RemoveAll<ISessionService>();
            services.AddSingleton<ISessionService>(Sessions);
            services.RemoveAll<ITotpService>();
            services.AddSingleton<ITotpService>(Totp);

            // Swap the Postgres-backed Data Protection keyring for an in-memory
            // one so tests never reach (and never need) the stubbed datasource.
            services.RemoveAll<PostgresXmlRepository>();
            services.RemoveAll<IConfigureOptions<KeyManagementOptions>>();
            var inMemoryRepo = new InMemoryXmlRepository();
            services.Configure<KeyManagementOptions>(o => o.XmlRepository = inMemoryRepo);
        });
    }
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

/// Settings service that pre-loads every value from <see cref="SettingDefaults.All"/>,
/// so tests can rely on the same defaults the app ships with. Overrides can be
/// set via <see cref="Set"/> before the factory is built.
public sealed class InMemorySettingsService : ISettingsService
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public InMemorySettingsService()
    {
        foreach (var d in SettingDefaults.All)
        {
            _values[d.Key] = d.Value;
        }
    }

    public void Set(string key, string value) => _values[key] = value;

    public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!_values.TryGetValue(key, out var raw))
        {
            throw new KeyNotFoundException($"Unknown setting '{key}' in InMemorySettingsService.");
        }
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(string))
        {
            return Task.FromResult((T)(object)raw);
        }
        if (target == typeof(bool))
        {
            return Task.FromResult((T)(object)bool.Parse(raw));
        }
        return Task.FromResult((T)Convert.ChangeType(raw, target, CultureInfo.InvariantCulture));
    }

    public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default)
    {
        _values[key] = value?.ToString() ?? "";
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
}

public sealed class FakeUserService : IUserService
{
    private readonly ConcurrentDictionary<Guid, ApplicationUser> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byEmail = new(StringComparer.OrdinalIgnoreCase);

    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_byId.Count);

    public Task<ApplicationUser?> CreateFirstAdminAsync(string email, string passwordHash, CancellationToken ct = default)
    {
        if (_byId.Count > 0)
        {
            return Task.FromResult<ApplicationUser?>(null);
        }
        var user = new ApplicationUser(Guid.NewGuid(), email, passwordHash, "Admin",
            DateTime.UtcNow, null, 0, null);
        _byId[user.Id] = user;
        _byEmail[email] = user.Id;
        return Task.FromResult<ApplicationUser?>(user);
    }

    public Task<ApplicationUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        if (_byEmail.TryGetValue(email, out var id) && _byId.TryGetValue(id, out var user))
        {
            return Task.FromResult<ApplicationUser?>(user);
        }
        return Task.FromResult<ApplicationUser?>(null);
    }

    public Task<ApplicationUser?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var u) ? u : null);

    public Task UpdatePasswordHashAsync(Guid userId, string newHash, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(userId, out var u))
        {
            _byId[userId] = u with { PasswordHash = newHash };
        }
        return Task.CompletedTask;
    }

    public Task RecordSuccessfulLoginAsync(Guid userId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(userId, out var u))
        {
            _byId[userId] = u with { LastLoginUtc = DateTime.UtcNow, FailedAttempts = 0, LockoutUntilUtc = null };
        }
        return Task.CompletedTask;
    }

    public Task<bool> RecordFailedLoginAsync(Guid userId, int maxAttempts, int windowSeconds, int lockoutDurationSeconds, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(userId, out var u))
        {
            return Task.FromResult(false);
        }
        var attempts = u.FailedAttempts + 1;
        var lockoutUntil = attempts >= maxAttempts ? DateTime.UtcNow.AddSeconds(lockoutDurationSeconds) : (DateTime?)null;
        _byId[userId] = u with { FailedAttempts = attempts, LockoutUntilUtc = lockoutUntil };
        return Task.FromResult(lockoutUntil.HasValue);
    }
}

public sealed class FakeSessionService : ISessionService
{
    public sealed record Entry(Guid Id, Guid UserId, string Amr, DateTime ExpiresUtc, DateTime LastSeenUtc, bool Revoked);

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    public Task<Guid> CreateAsync(Guid userId, string? ip, string? userAgent, TimeSpan lifetime, string amr, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        _sessions[id] = new Entry(id, userId, amr, DateTime.UtcNow.Add(lifetime), DateTime.UtcNow, false);
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
        var user = new ApplicationUser(e.UserId, "test@example.com", "", "Admin", DateTime.UtcNow, null, 0, null);
        return Task.FromResult<SessionValidation?>(new SessionValidation(e.Id, user, e.Amr, e.ExpiresUtc));
    }

    public Task TouchAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var e))
        {
            _sessions[sessionId] = e with { LastSeenUtc = DateTime.UtcNow };
        }
        return Task.CompletedTask;
    }

    public Task RevokeAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var e))
        {
            _sessions[sessionId] = e with { Revoked = true };
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
}

public sealed class FakeTotpService : ITotpService
{
    public bool Enabled { get; set; }

    public Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Enabled);
    public Task<TotpEnrollment> BeginEnrollAsync(Guid userId, string accountLabel, CancellationToken ct = default) =>
        Task.FromResult(new TotpEnrollment("JBSWY3DPEHPK3PXP", "otpauth://totp/Servicedesk:test?secret=JBSWY3DPEHPK3PXP"));
    public Task<IReadOnlyList<string>?> ConfirmEnrollAsync(Guid userId, string code, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>?>(new[] { "aaaa-bbbb-cccc-dddd" });
    public Task<TwoFactorResult> VerifyAsync(Guid userId, string code, CancellationToken ct = default) =>
        Task.FromResult(TwoFactorResult.Rejected);
    public Task DisableAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}
