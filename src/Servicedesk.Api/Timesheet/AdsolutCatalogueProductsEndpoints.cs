using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Timesheet;

/// Admin-facing endpoints for the Timesheet → Adsolut "VK Werkuren" article
/// manager (Settings → Timesheet → manage which catalogue products count as
/// billable work hours). Lists the mirrored Adsolut product catalogue, toggles
/// the per-product work-hours flag, exposes the catalogue sync state and a
/// manual "Sync now" trigger.
///
/// Gated RequireAdmin: the flag changes install-wide how the verkoopbon ↔
/// registered-hours matching is computed, so it is admin configuration — not an
/// agent-facing read. The catalogue sync shares the SalesReceipts opt-in.
public static class AdsolutCatalogueProductsEndpoints
{
    public static IEndpointRouteBuilder MapAdsolutCatalogueProductsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/timesheet/adsolut/catalogue-products")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("", ListProducts).WithName("ListAdsolutCatalogueProducts").WithOpenApi();
        group.MapGet("/state", GetState).WithName("GetAdsolutCatalogueProductsState").WithOpenApi();
        group.MapPost("/{id:guid}/work-hours", SetWorkHours).WithName("SetAdsolutCatalogueProductWorkHours").WithOpenApi();
        group.MapPost("/sync", TriggerSync).WithName("TriggerAdsolutCatalogueProductsSync").WithOpenApi();

        return group;
    }

    private static async Task<IResult> ListProducts(
        string? search,
        int? page,
        int? pageSize,
        string? sort,
        string? dir,
        bool? activeOnly,
        string? workHours,
        IAdsolutCatalogueProductRepository repo,
        CancellationToken ct)
    {
        var result = await repo.ListAsync(
            search, page ?? 1, pageSize ?? 50, sort, dir, activeOnly ?? false, workHours, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(ToDto),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    private static async Task<IResult> GetState(
        IAdsolutCatalogueProductRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpSalesReceiptsEnabled, ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpCatalogueProductsSyncIntervalMinutes, ct);
        if (intervalMinutes <= 0) intervalMinutes = 1440;

        var state = await repo.GetSyncStateAsync(ct);
        var total = await repo.GetCountAsync(ct);
        var workHoursCount = await repo.GetWorkHoursCountAsync(ct);
        var nextSyncUtc = Servicedesk.Infrastructure.Health.IntegrationsHealthAggregator
            .ComputeNextSyncUtc(state?.LastDeltaSyncUtc, Math.Max(5, intervalMinutes));

        return Results.Ok(new
        {
            enabled,
            intervalMinutes,
            totalMirrored = total,
            workHoursCount,
            lastFullSyncUtc = state?.LastFullSyncUtc,
            lastDeltaSyncUtc = state?.LastDeltaSyncUtc,
            lastError = state?.LastError,
            lastErrorUtc = state?.LastErrorUtc,
            productsSeen = state?.ProductsSeen ?? 0,
            productsUpserted = state?.ProductsUpserted ?? 0,
            updatedUtc = state?.UpdatedUtc,
            nextSyncUtc,
        });
    }

    public sealed record SetWorkHoursRequest(bool CountsAsWorkHours);

    private static async Task<IResult> SetWorkHours(
        Guid id,
        [FromBody] SetWorkHoursRequest req,
        HttpContext http,
        IAdsolutCatalogueProductRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        if (userId == Guid.Empty) return Results.Unauthorized();

        var ok = await repo.SetWorkHoursAsync(id, req.CountsAsWorkHours, userId, ct);
        if (!ok) return Results.NotFound(new { error = "not_found", message = "No catalogue product with that id." });

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.catalogue_products.work_hours_set",
            Actor: actor,
            ActorRole: role,
            Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { productId = id, countsAsWorkHours = req.CountsAsWorkHours }), ct);

        return Results.Ok(new { id, countsAsWorkHours = req.CountsAsWorkHours });
    }

    private static async Task<IResult> TriggerSync(
        HttpContext http,
        IAdsolutCatalogueProductsSyncSignal signal,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpSalesReceiptsEnabled, ct);
        if (!enabled)
        {
            return Results.BadRequest(new { error = "disabled", message = "Enable 'Pull sales receipts' (Settings → Integrations → Adsolut) first — the catalogue shares that toggle." });
        }
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.catalogue_products.sync_requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    private static object ToDto(AdsolutCatalogueProductRow p) => new
    {
        id = p.Id,
        code = p.Code,
        name = p.Name,
        serviceProduct = p.ServiceProduct,
        isActive = p.IsActive,
        blocked = p.Blocked,
        endOfSeries = p.EndOfSeries,
        countsAsWorkHours = p.CountsAsWorkHours,
        workHoursUpdatedUtc = p.WorkHoursUpdatedUtc,
        workHoursUpdatedByEmail = p.WorkHoursUpdatedByEmail,
        adsolutLastModified = p.AdsolutLastModified,
        syncedUtc = p.SyncedUtc,
    };
}
