using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Signatures;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Microsoft;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Signatures;

/// Resolves the `{{agent.*}}` values for a sending agent: per-user local
/// override fields win, Microsoft Entra ID fills the gaps (when enabled), and
/// the email always comes from the local account. The profile photo is fetched
/// from Entra once and cached as a content-addressed blob on the user row, so
/// steady-state sends never hit Graph for it. The whole resolved set is cached
/// in-memory for a short window so a busy mailbox doesn't re-query per send.
public sealed class SignatureVariableResolver : ISignatureVariableResolver
{
    // Internal perf cache only — the underlying data (Entra profile) changes
    // rarely. A profile edit calls Invalidate so the change is not hidden.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly IUserService _users;
    private readonly IAgentProfileRepository _profiles;
    private readonly IGraphDirectoryClient _directory;
    private readonly ISettingsService _settings;
    private readonly IBlobStore _blobs;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SignatureVariableResolver> _logger;

    public SignatureVariableResolver(
        IUserService users,
        IAgentProfileRepository profiles,
        IGraphDirectoryClient directory,
        ISettingsService settings,
        IBlobStore blobs,
        IMemoryCache cache,
        ILogger<SignatureVariableResolver> logger)
    {
        _users = users;
        _profiles = profiles;
        _directory = directory;
        _settings = settings;
        _blobs = blobs;
        _cache = cache;
        _logger = logger;
    }

    /// System / trigger mail has no human sender, so no person variables — any
    /// stray `{{agent.*}}` token in the system signature collapses to empty.
    public Task<SignatureVariables> ResolveSystemAsync(CancellationToken ct)
        => Task.FromResult(SignatureVariables.Empty);

    public async Task<SignatureVariables> ResolveForUserAsync(Guid userId, CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey(userId), out SignatureVariables? cached) && cached is not null)
            return cached;

        var vars = await ResolveFreshAsync(userId, ct);
        _cache.Set(CacheKey(userId), vars, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
        });
        return vars;
    }

    public void Invalidate(Guid userId) => _cache.Remove(CacheKey(userId));

    private async Task<SignatureVariables> ResolveFreshAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId, ct);
        if (user is null) return SignatureVariables.Empty;

        var profile = await _profiles.GetAsync(userId, ct);

        GraphUserProfile? entra = null;
        var entraEnabled = await _settings.GetAsync<bool>(SettingKeys.Signatures.EntraSyncEnabled, ct);
        if (entraEnabled && !string.IsNullOrWhiteSpace(user.ExternalSubject))
        {
            try
            {
                entra = await _directory.GetUserProfileAsync(user.ExternalSubject!, ct);
            }
            catch (Exception ex)
            {
                // Graph not configured / missing User.Read.All / transient — fall
                // back to local fields rather than blocking the mail send.
                _logger.LogDebug(ex,
                    "Entra profile lookup failed for user {UserId}; using local signature fields.", userId);
            }
        }

        var fullName = FirstNonEmpty(profile?.DisplayName, entra?.DisplayName, DeriveNameFromEmail(user.Email));
        var (firstName, lastName) = SplitName(fullName);
        var jobTitle = FirstNonEmpty(profile?.JobTitle, entra?.JobTitle);
        var phone = FirstNonEmpty(profile?.WorkPhone, entra?.BusinessPhone);
        var mobile = FirstNonEmpty(profile?.MobilePhone, entra?.MobilePhone);

        var (photoHash, photoMime) = await ResolvePhotoAsync(userId, user, profile, ct);

        return new SignatureVariables(
            FullName: fullName,
            FirstName: firstName,
            LastName: lastName,
            JobTitle: jobTitle,
            Email: user.Email,
            Phone: phone,
            Mobile: mobile,
            PhotoBlobHash: photoHash,
            PhotoMime: photoMime);
    }

    /// Local/cached photo wins. Otherwise, when photo sync is enabled, pull it
    /// from Entra once and persist it to the user row so future sends reuse it.
    private async Task<(string? Hash, string? Mime)> ResolvePhotoAsync(
        Guid userId, ApplicationUser user, AgentProfile? profile, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(profile?.PhotoBlobHash))
            return (profile!.PhotoBlobHash, profile.PhotoMime);

        var syncPhotos = await _settings.GetAsync<bool>(SettingKeys.Signatures.EntraSyncPhotos, ct);
        if (!syncPhotos || string.IsNullOrWhiteSpace(user.ExternalSubject))
            return (null, null);

        try
        {
            var photo = await _directory.GetUserPhotoAsync(user.ExternalSubject!, ct);
            if (photo is null || photo.Bytes.Length == 0) return (null, null);

            await using var ms = new MemoryStream(photo.Bytes, writable: false);
            var written = await _blobs.WriteAsync(ms, ct);
            await _profiles.SetPhotoAsync(userId, written.ContentHash, photo.ContentType, ct);
            await _profiles.StampEntraSyncedAsync(userId, ct);
            return (written.ContentHash, photo.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Entra photo fetch failed for user {UserId}; signature will omit the photo.", userId);
            return (null, null);
        }
    }

    private static string CacheKey(Guid userId) => $"signature-vars:{userId}";

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c!.Trim();
        return string.Empty;
    }

    private static (string First, string Last) SplitName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return (string.Empty, string.Empty);
        var trimmed = fullName.Trim();
        var idx = trimmed.IndexOf(' ');
        if (idx <= 0) return (trimmed, string.Empty);
        return (trimmed[..idx], trimmed[(idx + 1)..].Trim());
    }

    /// Last-resort display name when neither the local override nor Entra has
    /// one: turn "jan.peeters" → "Jan Peeters". Never used when a real name is
    /// available.
    private static string DeriveNameFromEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var at = email.IndexOf('@');
        var local = at > 0 ? email[..at] : email;
        var parts = local.Split(new[] { '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries);
        var words = parts
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpper(p[0], CultureInfo.InvariantCulture) + p[1..]);
        return string.Join(' ', words);
    }
}

public interface ISignatureVariableResolver
{
    Task<SignatureVariables> ResolveForUserAsync(Guid userId, CancellationToken ct);
    Task<SignatureVariables> ResolveSystemAsync(CancellationToken ct);
    void Invalidate(Guid userId);
}
