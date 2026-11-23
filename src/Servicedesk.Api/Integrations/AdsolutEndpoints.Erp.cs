using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

public static partial class AdsolutEndpoints
{
    private static void MapAdsolutErpRoutes(RouteGroupBuilder admin)
    {
        // ERP SalesReceipts (verkoopbonnen) mirror — admin controls on the
        // integration page. The toggle + interval are plain settings (edited
        // via the settings API); these endpoints expose the sync state +
        // discovered statuses, a "sync now" trigger, and the status-filter
        // selection.
        admin.MapGet("/erp/sales-receipts/state", GetErpSalesReceiptsState).WithName("GetAdsolutErpSalesReceiptsState");
        admin.MapPost("/erp/sales-receipts/sync", TriggerErpSalesReceiptsSync).WithName("TriggerAdsolutErpSalesReceiptsSync");
        admin.MapPut("/erp/sales-receipts/status-filter", SetErpSalesReceiptsStatusFilter).WithName("SetAdsolutErpSalesReceiptsStatusFilter");

        // ERP Orders (bestellingen) mirror — admin controls on the integration
        // page. Same shape as sales-receipts: state surface, "sync now", and
        // the DISPLAY-ONLY status filter (mirror always holds every status).
        admin.MapGet("/erp/orders/state", GetErpOrdersState).WithName("GetAdsolutErpOrdersState");
        admin.MapPost("/erp/orders/sync", TriggerErpOrdersSync).WithName("TriggerAdsolutErpOrdersSync");
        admin.MapPut("/erp/orders/status-filter", SetErpOrdersStatusFilter).WithName("SetAdsolutErpOrdersStatusFilter");
        admin.MapPut("/erp/orders/supplier-status-colors", SetErpOrdersSupplierStatusColors).WithName("SetAdsolutErpOrdersSupplierStatusColors");
        // ERP articles (artikels) mirror — feeds the Contract Articles list
        // (Contracts → Contract Articles). State surface + "sync now".
        admin.MapGet("/erp/articles/state", GetErpArticlesState).WithName("GetAdsolutErpArticlesState");
        admin.MapPost("/erp/articles/sync", TriggerErpArticlesSync).WithName("TriggerAdsolutErpArticlesSync");
        // ERP contracts (contracten) mirror — feeds the Contracts overview
        // (Contracts → Contracts overview). State surface + "sync now" + the
        // DISPLAY-ONLY status filter (mirror always holds every status).
        admin.MapGet("/erp/contracts/state", GetErpContractsState).WithName("GetAdsolutErpContractsState");
        admin.MapPost("/erp/contracts/sync", TriggerErpContractsSync).WithName("TriggerAdsolutErpContractsSync");
        admin.MapPut("/erp/contracts/status-filter", SetErpContractsStatusFilter).WithName("SetAdsolutErpContractsStatusFilter");
        admin.MapPut("/erp/contracts/status-colors", SetErpContractsStatusColors).WithName("SetAdsolutErpContractsStatusColors");
    }

    // ---- /erp/sales-receipts (verkoopbonnen) mirror controls -----------

    private static async Task<IResult> GetErpSalesReceiptsState(
        IAdsolutSalesReceiptRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpSalesReceiptsEnabled, ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpSalesReceiptsSyncIntervalMinutes, ct);
        if (intervalMinutes <= 0) intervalMinutes = 60;
        var filterRaw = await settings.GetAsync<string>(SettingKeys.Adsolut.ErpSalesReceiptsStatusFilter, ct) ?? string.Empty;
        var selected = filterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var state = await repo.GetSyncStateAsync(ct);
        var options = await repo.GetStatusOptionsAsync(ct);
        var total = await repo.GetCountAsync(ct);
        var nextSyncUtc = Servicedesk.Infrastructure.Health.IntegrationsHealthAggregator
            .ComputeNextSyncUtc(state?.LastDeltaSyncUtc, Math.Max(5, intervalMinutes));

        return Results.Ok(new
        {
            enabled,
            intervalMinutes,
            statusFilter = selected,
            totalMirrored = total,
            lastFullSyncUtc = state?.LastFullSyncUtc,
            lastDeltaSyncUtc = state?.LastDeltaSyncUtc,
            lastError = state?.LastError,
            lastErrorUtc = state?.LastErrorUtc,
            receiptsSeen = state?.ReceiptsSeen ?? 0,
            receiptsUpserted = state?.ReceiptsUpserted ?? 0,
            updatedUtc = state?.UpdatedUtc,
            nextSyncUtc,
            statusOptions = options.Select(o => new { code = o.Code, description = o.Description, count = o.Count }),
        });
    }

    private static async Task<IResult> TriggerErpSalesReceiptsSync(
        HttpContext http,
        IAdsolutSalesReceiptsSyncSignal signal,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpSalesReceiptsEnabled, ct);
        if (!enabled)
        {
            return Results.BadRequest(new { error = "disabled", message = "Enable 'Pull sales receipts' first." });
        }
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_sales_receipts.sync_requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    public sealed record SetErpStatusFilterRequest(string[]? Codes);

    private static async Task<IResult> SetErpSalesReceiptsStatusFilter(
        [FromBody] SetErpStatusFilterRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var codes = (req?.Codes ?? Array.Empty<string>())
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length is > 0 and <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var csv = string.Join(",", codes);
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Adsolut.ErpSalesReceiptsStatusFilter, csv, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_sales_receipts.status_filter_updated",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { codes }), ct);
        return Results.Ok(new { statusFilter = codes });
    }

    // ---- /erp/orders (bestellingen) mirror controls --------------------

    private static async Task<IResult> GetErpOrdersState(
        IAdsolutOrderRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpOrdersEnabled, ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpOrdersSyncIntervalMinutes, ct);
        if (intervalMinutes <= 0) intervalMinutes = 60;
        var filterRaw = await settings.GetAsync<string>(SettingKeys.Adsolut.ErpOrdersStatusFilter, ct) ?? string.Empty;
        var selected = filterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var state = await repo.GetSyncStateAsync(ct);
        var options = await repo.GetStatusOptionsAsync(ct);
        var supplierStatusOptions = await repo.GetSupplierStatusOptionsAsync(ct);
        var total = await repo.GetCountAsync(ct);
        var statusColors = await Servicedesk.Api.Orders.OrdersEndpoints.ResolveStatusColorsAsync(settings, ct);
        var nextSyncUtc = Servicedesk.Infrastructure.Health.IntegrationsHealthAggregator
            .ComputeNextSyncUtc(state?.LastDeltaSyncUtc, Math.Max(5, intervalMinutes));

        return Results.Ok(new
        {
            enabled,
            intervalMinutes,
            statusFilter = selected,
            totalMirrored = total,
            lastFullSyncUtc = state?.LastFullSyncUtc,
            lastDeltaSyncUtc = state?.LastDeltaSyncUtc,
            lastError = state?.LastError,
            lastErrorUtc = state?.LastErrorUtc,
            ordersSeen = state?.OrdersSeen ?? 0,
            ordersUpserted = state?.OrdersUpserted ?? 0,
            supplierOrdersSeen = state?.SupplierOrdersSeen ?? 0,
            supplierOrdersUpserted = state?.SupplierOrdersUpserted ?? 0,
            updatedUtc = state?.UpdatedUtc,
            nextSyncUtc,
            statusOptions = options.Select(o => new { code = o.Code, description = o.Description, count = o.Count }),
            supplierStatusOptions = supplierStatusOptions.Select(o => new { code = o.Code, description = o.Description, count = o.Count }),
            statusColors,
        });
    }

    private static async Task<IResult> TriggerErpOrdersSync(
        HttpContext http,
        IAdsolutOrdersSyncSignal signal,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpOrdersEnabled, ct);
        if (!enabled)
        {
            return Results.BadRequest(new { error = "disabled", message = "Enable 'Pull orders' first." });
        }
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_orders.sync_requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    // ---- /erp/articles (artikels) mirror controls ----------------------

    private static async Task<IResult> GetErpArticlesState(
        IAdsolutArticleRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpArticlesEnabled, ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpArticlesSyncIntervalMinutes, ct);
        if (intervalMinutes <= 0) intervalMinutes = 60;

        var state = await repo.GetSyncStateAsync(ct);
        var total = await repo.GetCountAsync(ct);
        var nextSyncUtc = Servicedesk.Infrastructure.Health.IntegrationsHealthAggregator
            .ComputeNextSyncUtc(state?.LastDeltaSyncUtc, Math.Max(5, intervalMinutes));

        return Results.Ok(new
        {
            enabled,
            intervalMinutes,
            totalMirrored = total,
            lastFullSyncUtc = state?.LastFullSyncUtc,
            lastDeltaSyncUtc = state?.LastDeltaSyncUtc,
            lastError = state?.LastError,
            lastErrorUtc = state?.LastErrorUtc,
            articlesSeen = state?.ArticlesSeen ?? 0,
            articlesUpserted = state?.ArticlesUpserted ?? 0,
            updatedUtc = state?.UpdatedUtc,
            nextSyncUtc,
        });
    }

    private static async Task<IResult> TriggerErpArticlesSync(
        HttpContext http,
        IAdsolutArticlesSyncSignal signal,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpArticlesEnabled, ct);
        if (!enabled)
        {
            return Results.BadRequest(new { error = "disabled", message = "Enable 'Pull articles' first." });
        }
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_articles.sync_requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    // ---- /erp/contracts (contracten) mirror controls -------------------

    private static async Task<IResult> GetErpContractsState(
        IAdsolutContractRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpContractsEnabled, ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Adsolut.ErpContractsSyncIntervalMinutes, ct);
        if (intervalMinutes <= 0) intervalMinutes = 60;
        var filterRaw = await settings.GetAsync<string>(SettingKeys.Adsolut.ErpContractsStatusFilter, ct) ?? string.Empty;
        var selected = filterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var state = await repo.GetSyncStateAsync(ct);
        var options = await repo.GetStatusOptionsAsync(ct);
        var total = await repo.GetCountAsync(ct);
        var statusColors = await Servicedesk.Api.Contracts.ContractsOverviewEndpoints.ResolveStatusColorsAsync(settings, ct);
        var nextSyncUtc = Servicedesk.Infrastructure.Health.IntegrationsHealthAggregator
            .ComputeNextSyncUtc(state?.LastDeltaSyncUtc, Math.Max(5, intervalMinutes));

        return Results.Ok(new
        {
            enabled,
            intervalMinutes,
            statusFilter = selected,
            totalMirrored = total,
            lastFullSyncUtc = state?.LastFullSyncUtc,
            lastDeltaSyncUtc = state?.LastDeltaSyncUtc,
            lastError = state?.LastError,
            lastErrorUtc = state?.LastErrorUtc,
            contractsSeen = state?.ContractsSeen ?? 0,
            contractsUpserted = state?.ContractsUpserted ?? 0,
            updatedUtc = state?.UpdatedUtc,
            nextSyncUtc,
            statusOptions = options.Select(o => new { code = o.Code, description = o.Description, count = o.Count }),
            statusColors,
        });
    }

    private static async Task<IResult> TriggerErpContractsSync(
        HttpContext http,
        IAdsolutContractsSyncSignal signal,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Adsolut.ErpContractsEnabled, ct);
        if (!enabled)
        {
            return Results.BadRequest(new { error = "disabled", message = "Enable 'Pull contracts' first." });
        }
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_contracts.sync_requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    private static async Task<IResult> SetErpContractsStatusFilter(
        [FromBody] SetErpStatusFilterRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var codes = (req?.Codes ?? Array.Empty<string>())
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length is > 0 and <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var csv = string.Join(",", codes);
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Adsolut.ErpContractsStatusFilter, csv, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_contracts.status_filter_updated",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { codes }), ct);
        return Results.Ok(new { statusFilter = codes });
    }

    private static async Task<IResult> SetErpContractsStatusColors(
        [FromBody] SetSupplierStatusColorsRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        // Whitelist: status code ≤32 chars, value a valid hex colour. Anything
        // else is dropped so the stored JSON only ever carries safe values.
        var clean = (req?.Colors ?? new Dictionary<string, string>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Key.Trim().Length <= 32
                         && kv.Value is not null && HexColorRegex.IsMatch(kv.Value.Trim()))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(clean);
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Adsolut.ErpContractsStatusColors, json, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_contracts.status_colors_updated",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { count = clean.Count }), ct);
        return Results.Ok(new { statusColors = clean });
    }

    private static async Task<IResult> SetErpOrdersStatusFilter(
        [FromBody] SetErpStatusFilterRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var codes = (req?.Codes ?? Array.Empty<string>())
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length is > 0 and <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var csv = string.Join(",", codes);
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Adsolut.ErpOrdersStatusFilter, csv, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_orders.status_filter_updated",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { codes }), ct);
        return Results.Ok(new { statusFilter = codes });
    }

    public sealed record SetSupplierStatusColorsRequest(Dictionary<string, string>? Colors);

    private static readonly Regex HexColorRegex =
        new("^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);

    private static async Task<IResult> SetErpOrdersSupplierStatusColors(
        [FromBody] SetSupplierStatusColorsRequest req,
        HttpContext http,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        // Whitelist: status code ≤32 chars, value a valid hex colour. Anything
        // else is dropped so the stored JSON only ever carries safe values.
        var clean = (req?.Colors ?? new Dictionary<string, string>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Key.Trim().Length <= 32
                         && kv.Value is not null && HexColorRegex.IsMatch(kv.Value.Trim()))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(clean);
        var (actor, role) = ActorContext.Resolve(http);
        await settings.SetAsync(SettingKeys.Adsolut.ErpOrdersSupplierStatusColors, json, actor, role, ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_orders.supplier_status_colors_updated",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { count = clean.Count }), ct);
        return Results.Ok(new { statusColors = clean });
    }
}
