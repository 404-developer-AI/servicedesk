using System.Text.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Orders;

/// Agent-facing endpoints for the Adsolut Orders (bestellingen) feature: the
/// mirrored orders overview (navbar → Assets → Orders), a per-order detail
/// (with lines), a manual "Sync orders" trigger (incremental — the worker
/// only pulls what changed), a per-row resync, the ticket editor's "::" order
/// picker, and the ticket ↔ order links.
///
/// The overview + picker + links are gated by the per-user
/// <c>adsolut_orders_enabled</c> flag in the SPA; consistent with the other
/// feature endpoints, the backend allows any authenticated agent (the flag
/// controls feature visibility, not security authorization — the data is
/// install-wide company order data, not per-user rows). The admin's display
/// status filter is applied to the overview, picker and global search.
public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders")
            .WithTags("Orders")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("", ListOrders).WithName("ListAdsolutOrders").WithOpenApi();
        group.MapGet("/picker", PickOrders).WithName("PickAdsolutOrders").WithOpenApi();
        group.MapGet("/{id:guid}", GetOrderDetail).WithName("GetAdsolutOrderDetail").WithOpenApi();
        group.MapPost("/sync", TriggerSync).WithName("TriggerAdsolutOrdersSync").WithOpenApi();
        group.MapPost("/{id:guid}/resync", ResyncOrder).WithName("ResyncAdsolutOrder").WithOpenApi();

        // Ticket ↔ order links. Separate group so it sits under the tickets
        // namespace but reuses the same RequireAgent gate.
        var ticketGroup = app.MapGroup("/api/tickets/{ticketId:guid}/orders")
            .WithTags("Orders")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        ticketGroup.MapGet("", ListTicketOrders).WithName("ListTicketOrders").WithOpenApi();
        ticketGroup.MapPost("/{orderId:guid}", LinkTicketOrder).WithName("LinkTicketOrder").WithOpenApi();
        ticketGroup.MapDelete("/{orderId:guid}", UnlinkTicketOrder).WithName("UnlinkTicketOrder").WithOpenApi();

        return group;
    }

    /// Resolve the admin's display status filter (DISPLAY-ONLY — the mirror
    /// holds every status). Empty = show all.
    private static async Task<IReadOnlyList<string>> ResolveStatusFilterAsync(
        ISettingsService settings, CancellationToken ct)
    {
        var raw = await settings.GetAsync<string>(SettingKeys.Adsolut.ErpOrdersStatusFilter, ct) ?? string.Empty;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<IResult> ListOrders(
        string? search,
        int? page,
        int? pageSize,
        string? sort,
        string? dir,
        IAdsolutOrderRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var statusFilter = await ResolveStatusFilterAsync(settings, ct);
        var result = await repo.ListAsync(search, page ?? 1, pageSize ?? 50, sort, dir, statusFilter, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(ToHeaderDto),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
            statusFilter,
        });
    }

    private static async Task<IResult> PickOrders(
        string? q,
        int? limit,
        IAdsolutOrderRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var statusFilter = await ResolveStatusFilterAsync(settings, ct);
        var rows = await repo.SearchForPickerAsync(q, statusFilter, limit ?? 10, ct);
        return Results.Ok(new { items = rows.Select(ToHeaderDto) });
    }

    private static async Task<IResult> GetOrderDetail(
        Guid id,
        IAdsolutOrderRepository repo,
        ISettingsService settings,
        CancellationToken ct)
    {
        var detail = await repo.GetDetailAsync(id, ct);
        if (detail is null) return Results.NotFound(new { error = "not_found" });
        var supplierLines = await repo.GetSupplierLinesForOrderAsync(id, ct);
        var statusColors = await ResolveStatusColorsAsync(settings, ct);
        return Results.Ok(ToDetailDto(detail, supplierLines, statusColors));
    }

    /// Parse the supplier-order status → colour map setting (JSON). Returns an
    /// empty map on empty/invalid JSON so the UI just falls back to neutral.
    public static async Task<IReadOnlyDictionary<string, string>> ResolveStatusColorsAsync(
        ISettingsService settings, CancellationToken ct)
    {
        var raw = (await settings.GetAsync<string>(SettingKeys.Adsolut.ErpOrdersSupplierStatusColors, ct) ?? string.Empty).Trim();
        if (raw.Length == 0) return new Dictionary<string, string>();
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            return map ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static async Task<IResult> TriggerSync(
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

    private static async Task<IResult> ResyncOrder(
        Guid id,
        HttpContext http,
        IAdsolutConnectionStore connections,
        IAdsolutOrdersClient client,
        IAdsolutOrderRepository repo,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Adsolut is not connected or no dossier is active." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutOrder? full;
        try
        {
            full = await client.GetByIdAsync(administrationId, id, ct);
        }
        catch (AdsolutApiException ex)
        {
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        if (full is null)
        {
            return Results.NotFound(new { error = "not_found_upstream", message = "Adsolut returned no order for this id." });
        }

        await repo.UpsertAsync(full, ct);

        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.erp_orders.resync_one",
            Actor: actor,
            ActorRole: role,
            Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { orderId = id }), ct);

        var detail = await repo.GetDetailAsync(id, ct);
        if (detail is null) return Results.Ok(new { ok = true });
        var supplierLines = await repo.GetSupplierLinesForOrderAsync(id, ct);
        var statusColors = await ResolveStatusColorsAsync(settings, ct);
        return Results.Ok(ToDetailDto(detail, supplierLines, statusColors));
    }

    // ---- ticket ↔ order links ------------------------------------------

    private static async Task<IResult> ListTicketOrders(
        Guid ticketId,
        IAdsolutOrderRepository repo,
        CancellationToken ct)
    {
        var rows = await repo.ListForTicketAsync(ticketId, ct);
        return Results.Ok(new { items = rows.Select(ToHeaderDto) });
    }

    private static async Task<IResult> LinkTicketOrder(
        Guid ticketId,
        Guid orderId,
        HttpContext http,
        IAdsolutOrderRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        Guid? userId = ActorContext.GetUserId(http) is var u && u != Guid.Empty ? u : null;
        bool created;
        try
        {
            created = await repo.LinkToTicketAsync(ticketId, orderId, userId, ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23503")
        {
            // FK violation — the ticket or the order does not exist in the mirror.
            return Results.NotFound(new { error = "not_found", message = "Ticket or order not found." });
        }

        if (created)
        {
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.order_link.created",
                Actor: actor,
                ActorRole: role,
                Target: ticketId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId, orderId }), ct);
        }

        var detail = await repo.GetDetailAsync(orderId, ct);
        return detail is null
            ? Results.Ok(new { ok = true, created })
            : Results.Ok(new { ok = true, created, order = ToHeaderDto(detail.Header) });
    }

    private static async Task<IResult> UnlinkTicketOrder(
        Guid ticketId,
        Guid orderId,
        HttpContext http,
        IAdsolutOrderRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        await repo.UnlinkFromTicketAsync(ticketId, orderId, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "ticket.order_link.removed",
            Actor: actor,
            ActorRole: role,
            Target: ticketId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { ticketId, orderId }), ct);
        return Results.Ok(new { ok = true });
    }

    // ---- DTO mapping ----------------------------------------------------

    private static object ToHeaderDto(AdsolutOrderRow o) => new
    {
        id = o.Id,
        docNr = o.DocNr,
        bookCode = o.BookCode,
        kluwerRef = o.KluwerRef,
        customerAdsolutId = o.CustomerAdsolutId,
        customerName = o.CustomerName,
        customerCode = o.CustomerCode,
        stateCode = o.StateCode,
        stateDescription = o.StateDescription,
        orderDate = o.OrderDate,
        requestedDeliveryDate = o.RequestedDeliveryDate,
        confirmedDeliveryDate = o.ConfirmedDeliveryDate,
        remark = o.Remark,
        internalMemo = o.InternalMemo,
        representativeName = o.RepresentativeName,
        totalExclVat = o.TotalExclVat,
        totalInclVat = o.TotalInclVat,
        currencyIso = o.CurrencyIso,
        ticketNumber = o.TicketNumber,
        adsolutCreatedUtc = o.AdsolutCreatedUtc,
        adsolutLastModified = o.AdsolutLastModified,
        syncedUtc = o.SyncedUtc,
    };

    private static object ToDetailDto(
        AdsolutOrderDetail d,
        IReadOnlyList<AdsolutSupplierOrderLineRow> supplierLines,
        IReadOnlyDictionary<string, string> statusColors) => new
    {
        header = ToHeaderDto(d.Header),
        lines = d.Lines.Select(l => new
        {
            id = l.Id,
            lineNr = l.LineNr,
            productCode = l.ProductCode,
            description = l.Description,
            quantity = l.Quantity,
            unitCode = l.UnitCode,
            unitDescription = l.UnitDescription,
            delivered = l.Delivered,
            grossUnitPrice = l.GrossUnitPrice,
            unitPrice = l.UnitPrice,
            discount1 = l.Discount1,
            discount2 = l.Discount2,
            priceExclVat = l.PriceExclVat,
            priceInclVat = l.PriceInclVat,
            vatCode = l.VatCode,
            vatDescription = l.VatDescription,
        }),
        // Supplier orders ("bestellingen") linked to this order (header-level
        // join). Each line carries the REAL procurement status + supplier +
        // date + delivered. statusColors maps the status code → hex.
        supplierLines = supplierLines.Select(s => new
        {
            id = s.Id,
            blDocNr = s.BlDocNr,
            blBookCode = s.BlBookCode,
            supplierName = s.SupplierName,
            supplierCode = s.SupplierCode,
            supplierOrderDate = s.SupplierOrderDate,
            lineNr = s.LineNr,
            productCode = s.ProductCode,
            name = s.Name,
            description = s.Description,
            quantity = s.Quantity,
            delivered = s.Delivered,
            unitCode = s.UnitCode,
            grossUnitPrice = s.GrossUnitPrice,
            unitPrice = s.UnitPrice,
            discount1 = s.Discount1,
            statusCode = s.StatusCode,
            warehouseName = s.WarehouseName,
            warehouseLocationName = s.WarehouseLocationName,
        }),
        statusColors,
    };
}
