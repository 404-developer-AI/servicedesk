using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Integrations.Trmm;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Assets page (v0.0.52). Agent + Admin only;
/// every route lives under <c>/api/assets</c>. Read-only; mutating
/// actions (mappings, sync trigger, secret CRUD) live on
/// <see cref="TrmmEndpoints"/> behind the admin policy.
///
/// Customer-portal visibility is intentionally not wired here in v0.0.52
/// — the schema already carries the company_id link so a future Customer
/// view can layer row-level filtering on top of these same endpoints.
public static class AssetsEndpoints
{
    public static IEndpointRouteBuilder MapAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets")
            .WithTags("Assets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", ListAssets).WithName("ListAssets").WithOpenApi();
        group.MapGet("/builds", ListBuilds).WithName("ListAssetBuilds").WithOpenApi();
        group.MapGet("/sync-state", GetSyncState).WithName("GetAssetsSyncState").WithOpenApi();
        group.MapGet("/{id:guid}", GetAsset).WithName("GetAsset").WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListAssets(
        HttpContext http,
        IAssetRepository repo,
        ISettingsService settings,
        string? search,
        string? type,
        string? builds,
        string? companyIds,
        bool? online,
        string? eolStatus,
        string? sort,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        // Per CLAUDE.md / global UI rules the page must work for an admin
        // looking at an install where the integration is not yet
        // configured — we still return an empty list rather than 409 so
        // the empty state on the SPA can render the setup CTA.
        _ = await settings.GetAsync<bool>(SettingKeys.Trmm.Enabled, ct);
        var warnThreshold = await settings.GetAsync<int>(SettingKeys.Eol.WarnThresholdDays, ct);

        var query = new AssetListQuery
        {
            Search = search,
            Type = NormalizeType(type),
            Builds = SplitCsv(builds),
            CompanyIds = ParseGuidCsv(companyIds),
            OnlineOnly = online,
            EolStatus = NormalizeEolStatus(eolStatus),
            Sort = string.IsNullOrWhiteSpace(sort) ? "build_desc" : sort,
            Page = page ?? 1,
            PageSize = pageSize ?? 50,
        };

        var result = await repo.ListAsync(query, warnThreshold, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(a => new
            {
                id = a.Id,
                trmmAgentId = a.TrmmAgentId,
                hostname = a.Hostname,
                agentType = a.AgentType,
                osName = a.OsName,
                osFamily = a.OsFamily,
                osBuild = a.OsBuild,
                lastSeenUtc = a.LastSeenUtc,
                online = a.Online,
                publicIp = a.PublicIp,
                trmmClientId = a.TrmmClientId,
                clientName = a.ClientName,
                clientCode = a.ClientCode,
                companyId = a.CompanyId,
                companyName = a.CompanyName,
                trmmSiteId = a.TrmmSiteId,
                siteName = a.SiteName,
                eolUtc = a.EolUtc,
                eolStatus = a.EolStatus,
            }),
            total = result.Total,
            page = query.Page,
            pageSize = query.PageSize,
            warnThresholdDays = Math.Clamp(warnThreshold, 1, 3650),
        });
    }

    private static async Task<IResult> ListBuilds(
        IAssetRepository repo, CancellationToken ct)
    {
        var builds = await repo.DistinctBuildsAsync(ct);
        return Results.Ok(new { items = builds });
    }

    private static async Task<IResult> GetSyncState(
        IAssetRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var state = await repo.GetSyncStateAsync(ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Trmm.SyncIntervalMinutes, ct);
        var enabled = await settings.GetAsync<bool>(SettingKeys.Trmm.Enabled, ct);
        return Results.Ok(new
        {
            enabled,
            lastSyncUtc = state.LastSyncUtc,
            lastStatus = state.LastStatus,
            lastError = state.LastError,
            syncIntervalMinutes = intervalMinutes,
        });
    }

    private static async Task<IResult> GetAsset(
        Guid id, IAssetRepository repo, ISettingsService settings, CancellationToken ct)
    {
        var warnThreshold = await settings.GetAsync<int>(SettingKeys.Eol.WarnThresholdDays, ct);
        var asset = await repo.GetByIdAsync(id, warnThreshold, ct);
        return asset is null ? Results.NotFound() : Results.Ok(new
        {
            id = asset.Id,
            trmmAgentId = asset.TrmmAgentId,
            hostname = asset.Hostname,
            agentType = asset.AgentType,
            osName = asset.OsName,
            osFamily = asset.OsFamily,
            osBuild = asset.OsBuild,
            lastSeenUtc = asset.LastSeenUtc,
            online = asset.Online,
            publicIp = asset.PublicIp,
            trmmClientId = asset.TrmmClientId,
            clientName = asset.ClientName,
            clientCode = asset.ClientCode,
            companyId = asset.CompanyId,
            companyName = asset.CompanyName,
            trmmSiteId = asset.TrmmSiteId,
            siteName = asset.SiteName,
            createdUtc = asset.CreatedUtc,
            updatedUtc = asset.UpdatedUtc,
            lastSyncUtc = asset.LastSyncUtc,
            eolUtc = asset.EolUtc,
            eolStatus = asset.EolStatus,
        });
    }

    private static string? NormalizeType(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return t switch
        {
            "server" => "server",
            "workstation" => "workstation",
            _ => null,
        };
    }

    private static string? NormalizeEolStatus(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return t switch
        {
            "active" or "soon" or "expired" or "unknown" => t,
            _ => null,
        };
    }

    private static List<string>? SplitCsv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
        return parts.Count == 0 ? null : parts;
    }

    private static List<Guid>? ParseGuidCsv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var list = new List<Guid>(8);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var g)) list.Add(g);
        }
        return list.Count == 0 ? null : list;
    }
}
