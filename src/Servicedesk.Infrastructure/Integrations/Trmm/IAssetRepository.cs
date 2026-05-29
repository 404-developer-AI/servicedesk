namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Read-only access to the TRMM mirror tables for the Assets page.
/// Dapper-backed hot path — the page is virtualised and an admin can hold
/// tens of thousands of agents in scope so filter + sort happen in SQL.
public interface IAssetRepository
{
    Task<AssetListResult> ListAsync(AssetListQuery query, int warnThresholdDays, CancellationToken ct);
    Task<IReadOnlyList<string>> DistinctBuildsAsync(CancellationToken ct);
    Task<AssetDetail?> GetByIdAsync(Guid id, int warnThresholdDays, CancellationToken ct);
    Task<TrmmSyncStateRow> GetSyncStateAsync(CancellationToken ct);

    Task<IReadOnlyList<AssetClientMappingRow>> ListClientMappingsAsync(CancellationToken ct);
    Task<bool> SetClientMappingAsync(
        long trmmClientId,
        Guid? companyId,
        bool clearOverride,
        CancellationToken ct);
}

public sealed class AssetListQuery
{
    public string? Search { get; init; }
    public string? Type { get; init; }
    public IReadOnlyList<string>? Builds { get; init; }
    public IReadOnlyList<Guid>? CompanyIds { get; init; }
    public bool? OnlineOnly { get; init; }
    /// <c>"active"</c> | <c>"soon"</c> | <c>"expired"</c> | <c>"unknown"</c> | null.
    public string? EolStatus { get; init; }
    public string Sort { get; init; } = "build_desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public class AssetListItem
{
    public Guid Id { get; set; }
    public string TrmmAgentId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string AgentType { get; set; } = "";
    public string? OsName { get; set; }
    public string? OsFamily { get; set; }
    public string? OsBuild { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool Online { get; set; }
    public string? PublicIp { get; set; }
    public long TrmmClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string? ClientCode { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public long TrmmSiteId { get; set; }
    public string SiteName { get; set; } = "";
    public DateTime? EolUtc { get; set; }
    public string EolStatus { get; set; } = "unknown";
}

public sealed record AssetListResult(
    IReadOnlyList<AssetListItem> Items,
    int Total);

public sealed class AssetDetail
{
    public Guid Id { get; set; }
    public string TrmmAgentId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string AgentType { get; set; } = "";
    public string? OsName { get; set; }
    public string? OsFamily { get; set; }
    public string? OsBuild { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool Online { get; set; }
    public string? PublicIp { get; set; }
    public long TrmmClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string? ClientCode { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public long TrmmSiteId { get; set; }
    public string SiteName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime LastSyncUtc { get; set; }
    public DateTime? EolUtc { get; set; }
    public string EolStatus { get; set; } = "unknown";
}

public sealed class TrmmSyncStateRow
{
    public DateTime? LastSyncUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public string LastCountsJson { get; set; } = "{}";
}

public sealed class AssetClientMappingRow
{
    public long TrmmClientId { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public bool AutoMatched { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyCode { get; set; }
    public int AgentCount { get; set; }
}
