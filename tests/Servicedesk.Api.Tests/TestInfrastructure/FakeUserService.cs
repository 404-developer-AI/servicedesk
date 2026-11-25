using System.Collections.Concurrent;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// In-memory <see cref="IUserService"/> for the security-baseline host, with
/// per-user feature-flag seams.
public sealed class FakeUserService : IUserService
{
    private readonly ConcurrentDictionary<Guid, ApplicationUser> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byEmail = new(StringComparer.OrdinalIgnoreCase);

    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_byId.Count);

    /// Forget every user and per-user flag (SharedHostFixture resets between tests).
    public void Reset()
    {
        _byId.Clear();
        _byEmail.Clear();
        TimesheetManagers.Clear();
        IsoFlagsByUser.Clear();
        AssetsEnabled.Clear();
        AdsolutTimesheetEnabled.Clear();
        AdsolutOrdersEnabled.Clear();
        TimesheetBackofficeEnabled.Clear();
        BackofficeFormsEnabled.Clear();
        CalendarPlanningEnabled.Clear();
        OrderManagementEnabled.Clear();
        CampaignsEnabled.Clear();
        InsightsEnabled.Clear();
    }

    /// Test seam: add a Local user row with any role (v0.1.0 — portal tests
    /// need a Customer row the agent login must refuse).
    public ApplicationUser Add(string email, string passwordHash, string role)
        => AddWithId(Guid.NewGuid(), email, passwordHash, role);

    /// Test seam (v0.1.3): add a row under a caller-chosen id so a test can
    /// line the user store up with a pre-minted fake session.
    public ApplicationUser AddWithId(Guid id, string email, string passwordHash, string role)
    {
        var user = new ApplicationUser(id, email, passwordHash, role,
            DateTime.UtcNow, null, 0, null, AuthModes.Local, null, null, true);
        _byId[user.Id] = user;
        _byEmail[email] = user.Id;
        return user;
    }

    public Task<ApplicationUser?> CreateFirstAdminAsync(string email, string passwordHash, CancellationToken ct = default)
    {
        if (_byId.Count > 0)
        {
            return Task.FromResult<ApplicationUser?>(null);
        }
        var user = new ApplicationUser(Guid.NewGuid(), email, passwordHash, "Admin",
            DateTime.UtcNow, null, 0, null, AuthModes.Local, null, null, true);
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

    public Task<IReadOnlyList<AgentUser>> ListAgentsAsync(CancellationToken ct = default)
    {
        var agents = _byId.Values
            .Where(u => u.RoleName is "Agent" or "Admin")
            .Select(u => new AgentUser(u.Id, u.Email, u.RoleName))
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentUser>>(agents);
    }

    public Task<IReadOnlyList<AgentUser>> SearchAgentsAsync(string? search, int limit, CancellationToken ct = default)
    {
        var effectiveLimit = limit <= 0 ? 20 : Math.Min(limit, 50);
        var trimmed = (search ?? string.Empty).Trim();
        var query = _byId.Values.Where(u => u.RoleName is "Agent" or "Admin");
        if (trimmed.Length > 0)
        {
            query = query.Where(u => u.Email.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }
        var agents = query
            .OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .Take(effectiveLimit)
            .Select(u => new AgentUser(u.Id, u.Email, u.RoleName))
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentUser>>(agents);
    }

    public Task<IReadOnlyList<Guid>> FilterAgentIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids is null || ids.Count == 0) return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        var allowed = new HashSet<Guid>(_byId.Values
            .Where(u => u.RoleName is "Agent" or "Admin")
            .Select(u => u.Id));
        var filtered = ids.Distinct().Where(id => allowed.Contains(id)).ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(filtered);
    }

    public Task<ApplicationUser?> FindByExternalAsync(string provider, string subject, CancellationToken ct = default)
    {
        var match = _byId.Values.FirstOrDefault(u =>
            string.Equals(u.ExternalProvider, provider, StringComparison.Ordinal) &&
            string.Equals(u.ExternalSubject, subject, StringComparison.Ordinal));
        return Task.FromResult<ApplicationUser?>(match);
    }

    public Task MarkInactiveAsync(Guid userId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(userId, out var u))
        {
            _byId[userId] = u with { IsActive = false };
        }
        return Task.CompletedTask;
    }

    public Task<TimesheetFlags> GetTimesheetFlagsAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(TimesheetManagers.ContainsKey(userId) ? new TimesheetFlags(true, true) : TimesheetFlags.None);

    /// Users that carry the Timesheet manager flag (Insights Workflow gate).
    public ConcurrentDictionary<Guid, bool> TimesheetManagers { get; } = new();

    /// Test seam (v0.1.27): per-user ISO 27001 flags.
    public ConcurrentDictionary<Guid, IsoFlags> IsoFlagsByUser { get; } = new();

    public Task<IsoFlags> GetIsoFlagsAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(IsoFlagsByUser.TryGetValue(userId, out var f) ? f : IsoFlags.None);

    public Task<bool> GetKbEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> GetSearchEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> GetActivityFeedEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public ConcurrentDictionary<Guid, bool> AssetsEnabled { get; } = new();

    public Task<bool> GetAssetsEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(AssetsEnabled.ContainsKey(userId));

    public ConcurrentDictionary<Guid, bool> AdsolutTimesheetEnabled { get; } = new();

    public Task<bool> GetAdsolutTimesheetEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(AdsolutTimesheetEnabled.ContainsKey(userId));

    public ConcurrentDictionary<Guid, bool> AdsolutOrdersEnabled { get; } = new();

    public Task<bool> GetAdsolutOrdersEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(AdsolutOrdersEnabled.ContainsKey(userId));

    public ConcurrentDictionary<Guid, bool> TimesheetBackofficeEnabled { get; } = new();

    public Task<bool> GetTimesheetBackofficeEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(TimesheetBackofficeEnabled.ContainsKey(userId));

    public ConcurrentDictionary<Guid, bool> BackofficeFormsEnabled { get; } = new();

    public Task<bool> GetBackofficeFormsEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(BackofficeFormsEnabled.ContainsKey(userId));

    public ConcurrentDictionary<Guid, bool> CalendarPlanningEnabled { get; } = new();

    public Task<bool> GetCalendarPlanningEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(CalendarPlanningEnabled.ContainsKey(userId));

    public ConcurrentDictionary<Guid, bool> OrderManagementEnabled { get; } = new();

    public Task<bool> GetOrderManagementEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(OrderManagementEnabled.ContainsKey(userId));

    public ConcurrentDictionary<Guid, bool> CampaignsEnabled { get; } = new();

    public Task<bool> GetCampaignsEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(CampaignsEnabled.ContainsKey(userId));

    public Task<bool> GetStatisticsReadEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> GetStatisticsWriteEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> GetContractsEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// Test seam (v0.1.13): user ids that carry the Insights feature flag.
    public ConcurrentDictionary<Guid, bool> InsightsEnabled { get; } = new();

    public Task<bool> GetInsightsEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(InsightsEnabled.ContainsKey(userId));

    public Task<bool> GetFeedbackEnabledAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<FeedbackAccessInfo> GetFeedbackAccessAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(default(FeedbackAccessInfo));

    public Task<IReadOnlySet<string>> GetSearchFeatureFlagsAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}
