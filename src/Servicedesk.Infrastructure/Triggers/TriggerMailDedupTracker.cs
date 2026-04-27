using Microsoft.Extensions.Caching.Memory;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Triggers;

/// In-memory dedup window for mail-actions emitted by triggers. When the
/// same trigger fires repeatedly on the same ticket — typically because an
/// agent is rapidly toggling fields that all match the same condition —
/// only the first mail-action goes out; subsequent ones short-circuit
/// inside the (Block 3) mail-action handler via
/// <see cref="ShouldSendAsync"/>.
///
/// The window length is read from the
/// <c>Triggers.MailDedupWindowMinutes</c> setting at every check so admins
/// can tune it without restarting. A non-positive value disables the
/// dedup entirely.
public sealed class TriggerMailDedupTracker
{
    private readonly IMemoryCache _cache;
    private readonly ISettingsService _settings;

    public TriggerMailDedupTracker(IMemoryCache cache, ISettingsService settings)
    {
        _cache = cache;
        _settings = settings;
    }

    /// Returns <c>true</c> when the mail-action should be sent (and records
    /// the fingerprint so the next caller within the window is told to
    /// skip). Returns <c>false</c> when a fingerprint match is already
    /// active. <paramref name="actionFingerprint"/> identifies the
    /// individual mail-action within the trigger; using the action-index
    /// keeps two distinct send-mail actions on the same trigger from
    /// shadowing each other.
    public async Task<bool> ShouldSendAsync(
        Guid triggerId,
        Guid ticketId,
        string actionFingerprint,
        CancellationToken ct)
    {
        var minutes = await _settings.GetAsync<int>(SettingKeys.Triggers.MailDedupWindowMinutes, ct);
        if (minutes <= 0) return true;

        var key = MakeKey(triggerId, ticketId, actionFingerprint);
        if (_cache.TryGetValue(key, out _)) return false;

        _cache.Set(key, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(minutes),
        });
        return true;
    }

    private static string MakeKey(Guid triggerId, Guid ticketId, string actionFingerprint)
        => $"trigger-mail:{triggerId:N}:{ticketId:N}:{actionFingerprint}";
}
