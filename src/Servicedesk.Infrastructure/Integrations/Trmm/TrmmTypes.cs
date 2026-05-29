namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// One TRMM client as parsed from <c>GET /clients/</c>. The display name
/// is the raw upstream string; <see cref="Code"/> is the optional
/// bracketed prefix extracted from <c>[CODE] Customer Name</c> — matched
/// case-insensitively against <c>companies.code</c> for auto-linking.
public sealed record TrmmClient(
    long Id,
    string Name,
    string? Code);

public sealed record TrmmSite(
    long Id,
    long ClientId,
    string Name);

/// Subset of TRMM agent fields the Assets page consumes. Anything beyond
/// these is intentionally not mirrored in v0.0.52 — adding a column is
/// cheaper than backfilling a column we ended up not needing.
///
/// TRMM's listing endpoint (<c>/agents/</c>) uses a lightweight
/// "table" serializer that flattens the client/site relation to
/// <c>client_name</c> + <c>site_name</c> strings — no FK ids. The
/// detail endpoint (<c>/agents/{id}/</c>) uses a fuller serializer
/// where <c>client</c>/<c>site</c> are integer ids. The parser
/// captures whichever side TRMM emits; the sync service resolves the
/// missing id by name against the just-upserted clients/sites tables.
public sealed record TrmmAgent(
    string AgentId,
    string Hostname,
    string AgentType,
    string? OsName,
    string? OsFamily,
    string? OsBuild,
    DateTime? LastSeenUtc,
    bool Online,
    string? PublicIp,
    long? ClientId,
    string? ClientName,
    long? SiteId,
    string? SiteName);

/// Result of <see cref="ITrmmApiClient.TestConnectionAsync"/>. Carries the
/// upstream version (when discoverable) + the resolved client count so the
/// admin sees more than just "OK" on success.
public sealed record TrmmConnectionTestResult(
    bool Success,
    string? Version,
    int ClientCount,
    int LatencyMs,
    string? ErrorCode,
    string? ErrorMessage);
