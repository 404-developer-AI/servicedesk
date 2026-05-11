using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Coordinates the three Telavox setup writes — test-connection,
/// provision-agent, revoke-agent — across the API client + secret store
/// + link store + audit log.
///
/// Provision flow ordering is deliberate: PAPI <i>create</i> first, then
/// secrets-write, then DB-row-write. If the secrets or DB step fails, we
/// best-effort revert the upstream api-user so Telavox isn't left with an
/// orphaned token holding an admin's seat. Cancellation tokens for the
/// rollback path use <see cref="CancellationToken.None"/> on purpose: a
/// cancelled provision still needs cleanup.
public sealed class TelavoxProvisioningService : ITelavoxProvisioningService
{
    private readonly ITelavoxApiClient _api;
    private readonly ITelavoxAgentLinkStore _links;
    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly IUserService _users;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TelavoxProvisioningService> _logger;

    public TelavoxProvisioningService(
        ITelavoxApiClient api,
        ITelavoxAgentLinkStore links,
        IProtectedSecretStore secrets,
        ISettingsService settings,
        IUserService users,
        IAuditLogger audit,
        ILogger<TelavoxProvisioningService> logger)
    {
        _api = api;
        _links = links;
        _secrets = secrets;
        _settings = settings;
        _users = users;
        _audit = audit;
        _logger = logger;
    }

    public async Task<TelavoxTestConnectionResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var customers = await _api.ListCustomersAsync(ct);
        return new TelavoxTestConnectionResult(customers);
    }

    public async Task<TelavoxAgentLink> ProvisionAgentAsync(
        TelavoxProvisionAgentRequest request, CancellationToken ct = default)
    {
        if (request.UserId == Guid.Empty)
            throw new ArgumentException("UserId is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TelavoxExtension))
            throw new ArgumentException("TelavoxExtension is required", nameof(request));

        var customerId = (await _settings.GetAsync<string>(SettingKeys.Telavox.PartnerCustomerId, ct)
            ?? string.Empty).Trim();
        if (customerId.Length == 0)
        {
            throw new InvalidOperationException(
                "Telavox.PartnerCustomerId is not configured. Pin a customer-id from the test-connection dropdown first.");
        }

        var user = await _users.FindByIdAsync(request.UserId, ct)
            ?? throw new InvalidOperationException("User not found.");
        if (!user.IsActive)
        {
            throw new InvalidOperationException(
                "User is not active; activate the user before linking a Telavox extension.");
        }
        if (!string.Equals(user.RoleName, "Admin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(user.RoleName, "Agent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Only Admin or Agent users can be linked to a Telavox extension.");
        }

        // Flow ordering: CreateApiUser first (most likely failure mode is
        // a Telavox transient/auth error — leave SD state untouched).
        // Only after the new api-user is in hand do we overwrite the
        // protected secret + DB row. The OLD upstream api-user, if any,
        // is revoked LAST and best-effort: if that step fails the worst
        // case is one stale row in Telavox's admin UI, while the
        // SD-side state is already consistent.
        var existing = await _links.GetByUserIdAsync(request.UserId, ct);

        // Collision-resistant synthetic api-user name. PAPI takes a
        // free-form `name` query-param at create-time and stores it as a
        // human-readable label (swagger example: "Alfreds Api User"); the
        // unique key returned in the body is Telavox-generated. Keep the
        // name short + word-shaped — a long all-hex name like
        // sd-agent-<32-hex>-<10-digit-epoch> triggered a generic PAPI 500
        // on first install, almost certainly tripping an undocumented
        // length or anti-abuse heuristic. 8 hex chars of the user-id is
        // unique enough to identify the SD-side row; base36 epoch keeps a
        // re-provision attempt distinct from the prior orphan without
        // ballooning the string.
        var shortUser = request.UserId.ToString("N").Substring(0, 8);
        var shortTime = ToBase36(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var capiName = $"Servicedesk {shortUser} {shortTime}";

        var created = await _api.CreateApiUserAsync(customerId, capiName, ct);

        TelavoxAgentLink link;
        try
        {
            // The secret key is keyed on userId so SetAsync overwrites any
            // stale value from a previous link in one atomic step — no
            // separate delete pass needed.
            await _secrets.SetAsync(
                ProtectedSecretKeys.TelavoxAgentCapiToken(request.UserId),
                created.Token,
                ct);

            link = new TelavoxAgentLink(
                Id: Guid.Empty,
                UserId: request.UserId,
                TelavoxExtension: request.TelavoxExtension.Trim(),
                TelavoxUserId: created.UserId,
                CapiUserName: created.Name,
                ProvisionedUtc: DateTime.UtcNow,
                LastPollUtc: null,
                LastPollError: null,
                ConsecutiveErrors: 0);
            await _links.UpsertAsync(link, ct);
        }
        catch (Exception ex)
        {
            // SetAsync or UpsertAsync failed AFTER the new upstream api-user
            // was created. Roll the new upstream back so Telavox isn't
            // left with an orphan; do NOT touch the OLD api-user/secret —
            // those are still the live link until/unless re-provision
            // succeeds. Use CancellationToken.None so a cancelled provision
            // still attempts cleanup.
            _logger.LogError(ex,
                "Telavox provisioning failed after CAPI-user create; rolling back the new upstream api-user for user {UserId}.",
                request.UserId);
            try { await _api.DeleteApiUserAsync(customerId, created.UserId, CancellationToken.None); }
            catch (Exception rbEx)
            {
                _logger.LogWarning(rbEx,
                    "Telavox upstream rollback failed for user {UserId}; admin must manually revoke api-user {ApiUserKey}.",
                    request.UserId, created.UserId);
            }
            // The new secret may or may not have been written. If old
            // existed and was overwritten by SetAsync at this point the
            // old token value is unrecoverable; the polling worker will
            // surface 401s and the admin will re-provision. Acceptable
            // failure mode given how rarely SetAsync/UpsertAsync fail.
            throw;
        }

        // Success path: new state is fully committed. Now best-effort
        // delete the OLD upstream api-user, if any, so Telavox isn't left
        // with an orphan. Old protected_secret was already overwritten by
        // SetAsync above (userId-keyed), so no secret-cleanup needed. The
        // api-user *key* (TelavoxUserId) — not the human-readable name —
        // is what DELETE takes as path-param per the PAPI swagger.
        if (existing is not null
            && !string.Equals(existing.TelavoxUserId, created.UserId, StringComparison.OrdinalIgnoreCase))
        {
            try { await _api.DeleteApiUserAsync(customerId, existing.TelavoxUserId, ct); }
            catch (TelavoxApiException ex)
            {
                _logger.LogWarning(ex,
                    "Telavox cleanup of stale api-user {ApiUserKey} failed for user {UserId}; SD-side link is healthy but Telavox admin may show an orphan row.",
                    existing.TelavoxUserId, request.UserId);
            }
        }

        await _audit.LogAsync(new AuditEvent(
            EventType: TelavoxEventTypes.AgentProvisioned,
            Actor: request.Actor,
            ActorRole: request.ActorRole,
            Target: request.UserId.ToString(),
            Payload: new
            {
                userId = request.UserId,
                telavoxExtension = link.TelavoxExtension,
                telavoxUserId = link.TelavoxUserId,
                capiUserName = link.CapiUserName,
                replacedPriorLink = existing is not null,
            }), ct);

        return link;
    }

    /// Sentinel value written into <see cref="TelavoxAgentLink.TelavoxUserId"/>
    /// for manual-link rows. The revoke path treats this as "skip upstream
    /// PAPI DELETE" — there's no api-user to revoke because the token was
    /// minted outside our PAPI integration.
    internal const string ManualLinkSentinel = "manual";

    public async Task<TelavoxAgentLink> ProvisionAgentManualAsync(
        TelavoxProvisionAgentManualRequest request, CancellationToken ct = default)
    {
        if (request.UserId == Guid.Empty)
            throw new ArgumentException("UserId is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TelavoxExtension))
            throw new ArgumentException("TelavoxExtension is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.CapiToken))
            throw new ArgumentException("CapiToken is required", nameof(request));

        // Same user-state checks as the PAPI flow — manual-link does not
        // bypass active/role validation; only the upstream provisioning
        // step is replaced.
        var user = await _users.FindByIdAsync(request.UserId, ct)
            ?? throw new InvalidOperationException("User not found.");
        if (!user.IsActive)
        {
            throw new InvalidOperationException(
                "User is not active; activate the user before linking a Telavox extension.");
        }
        if (!string.Equals(user.RoleName, "Admin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(user.RoleName, "Agent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Only Admin or Agent users can be linked to a Telavox extension.");
        }

        var existing = await _links.GetByUserIdAsync(request.UserId, ct);

        // Store the supplied bearer in protected_secrets first. SetAsync
        // overwrites any prior value for the userId-keyed slot, so a
        // re-link (e.g. agent generated a fresh token in Telavox webapp)
        // is a one-step swap.
        await _secrets.SetAsync(
            ProtectedSecretKeys.TelavoxAgentCapiToken(request.UserId),
            request.CapiToken.Trim(),
            ct);

        var link = new TelavoxAgentLink(
            Id: Guid.Empty,
            UserId: request.UserId,
            TelavoxExtension: request.TelavoxExtension.Trim(),
            TelavoxUserId: ManualLinkSentinel,
            CapiUserName: $"manual ({user.Email})",
            ProvisionedUtc: DateTime.UtcNow,
            LastPollUtc: null,
            LastPollError: null,
            ConsecutiveErrors: 0);
        await _links.UpsertAsync(link, ct);

        // Best-effort: if the prior link was PAPI-minted (TelavoxUserId !=
        // ManualLinkSentinel and not empty), revoke it upstream so Telavox
        // doesn't keep an orphan. Failure here is logged-and-swallowed —
        // SD-side state is already consistent.
        if (existing is not null
            && !string.IsNullOrEmpty(existing.TelavoxUserId)
            && !string.Equals(existing.TelavoxUserId, ManualLinkSentinel, StringComparison.OrdinalIgnoreCase))
        {
            var customerId = (await _settings.GetAsync<string>(SettingKeys.Telavox.PartnerCustomerId, ct)
                ?? string.Empty).Trim();
            if (customerId.Length > 0)
            {
                try { await _api.DeleteApiUserAsync(customerId, existing.TelavoxUserId, ct); }
                catch (TelavoxApiException ex)
                {
                    _logger.LogWarning(ex,
                        "Manual-link cleanup of prior PAPI api-user {ApiUserKey} for user {UserId} failed; Telavox admin may show an orphan row.",
                        existing.TelavoxUserId, request.UserId);
                }
            }
        }

        await _audit.LogAsync(new AuditEvent(
            EventType: TelavoxEventTypes.AgentProvisionedManually,
            Actor: request.Actor,
            ActorRole: request.ActorRole,
            Target: request.UserId.ToString(),
            Payload: new
            {
                userId = request.UserId,
                telavoxExtension = link.TelavoxExtension,
                replacedPriorLink = existing is not null,
                priorLinkWasPapi = existing is not null
                    && !string.Equals(existing.TelavoxUserId, ManualLinkSentinel, StringComparison.OrdinalIgnoreCase),
            }), ct);

        return link;
    }

    /// .NET's <see cref="Convert.ToString(long, int)"/> only supports
    /// bases 2/8/10/16; base36 has to be hand-rolled. Used to compress
    /// the unix-second timestamp into a 6-7 char suffix on the synthetic
    /// api-user name so retries stay collision-free without bloating the
    /// human-readable label.
    private static string ToBase36(long value)
    {
        if (value == 0) return "0";
        const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        var n = Math.Abs(value);
        var sb = new System.Text.StringBuilder();
        while (n > 0)
        {
            sb.Insert(0, Alphabet[(int)(n % 36)]);
            n /= 36;
        }
        return value < 0 ? "-" + sb : sb.ToString();
    }

    public async Task RevokeAgentAsync(
        Guid userId, string actor, string actorRole, CancellationToken ct = default)
    {
        var link = await _links.GetByUserIdAsync(userId, ct);
        if (link is null) return;

        var isManualLink = string.Equals(link.TelavoxUserId, ManualLinkSentinel, StringComparison.OrdinalIgnoreCase);
        var customerId = (await _settings.GetAsync<string>(SettingKeys.Telavox.PartnerCustomerId, ct)
            ?? string.Empty).Trim();
        if (isManualLink)
        {
            // Token was minted outside PAPI (admin pasted it from the
            // Telavox webapp); SD has nothing to revoke upstream. The
            // admin remains responsible for invalidating the token in
            // their Telavox webapp if they need to fully kill access.
            _logger.LogInformation(
                "Telavox revoke for user {UserId} is local-only — link was manual; admin must invalidate the CAPI token in Telavox webapp separately.",
                userId);
        }
        else if (customerId.Length > 0)
        {
            try { await _api.DeleteApiUserAsync(customerId, link.TelavoxUserId, ct); }
            catch (TelavoxApiException ex)
            {
                _logger.LogWarning(ex,
                    "Telavox revoke for user {UserId} failed upstream; continuing with local cleanup.",
                    userId);
            }
        }
        else
        {
            // Customer-id was cleared after the link existed — Telavox-side
            // cleanup is impossible without a customer-id, but local
            // cleanup must still run so the row doesn't ghost-poll.
            _logger.LogWarning(
                "Telavox.PartnerCustomerId is empty during revoke for user {UserId}; skipping upstream PAPI delete.",
                userId);
        }

        try { await _secrets.DeleteAsync(ProtectedSecretKeys.TelavoxAgentCapiToken(userId), ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete CAPI secret for user {UserId} during revoke; row deletion still continues.",
                userId);
        }

        await _links.DeleteByUserIdAsync(userId, ct);

        await _audit.LogAsync(new AuditEvent(
            EventType: TelavoxEventTypes.AgentRevoked,
            Actor: actor,
            ActorRole: actorRole,
            Target: userId.ToString(),
            Payload: new
            {
                userId,
                telavoxExtension = link.TelavoxExtension,
                telavoxUserId = link.TelavoxUserId,
                capiUserName = link.CapiUserName,
            }), ct);
    }
}
