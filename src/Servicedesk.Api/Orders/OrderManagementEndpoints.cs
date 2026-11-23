using System.Security.Claims;
using System.Text.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.TdSynnex;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Api.Orders;

/// Agent-facing Order Management (v0.1.59): the TD SYNNEX order dashboard
/// (stats, list, detail, picker, "Sync now") and the manual ticket ↔ order
/// links behind the ticket's order chip.
///
/// Access: RequireAgent + the per-user <c>order_management_enabled</c> flag
/// on every route (403 when off, admins included). Ticket routes additionally
/// load the ticket through <see cref="TicketAccess"/> (404 outside the
/// caller's queues), and an order's linked-ticket list is filtered through
/// <see cref="QueueAccessScope"/> with a visible hidden-count.
public static class OrderManagementEndpoints
{
    private const int ChangeHistoryOnDetail = 25;
    private const int ChangeHistoryOnTicket = 5;

    public static IEndpointRouteBuilder MapOrderManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/order-management")
            .WithTags("Order Management")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.OrderManagement);

        group.MapGet("/summary", GetSummary).WithName("GetOrderManagementSummary");
        group.MapGet("/orders", ListOrders).WithName("ListTdSynnexOrders");
        group.MapGet("/orders/picker", PickOrders).WithName("PickTdSynnexOrders");
        group.MapGet("/orders/{id:guid}", GetOrder).WithName("GetTdSynnexOrder");
        group.MapPost("/sync", SyncNow).WithName("SyncTdSynnexOrders");

        var ticketGroup = app.MapGroup("/api/tickets/{ticketId:guid}/tdsynnex-orders")
            .WithTags("Order Management")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireFeature(UserFeature.OrderManagement);

        ticketGroup.MapGet("", ListTicketOrders).WithName("ListTicketTdSynnexOrders");
        ticketGroup.MapPost("/{orderId:guid}", LinkTicketOrder).WithName("LinkTicketTdSynnexOrder");
        ticketGroup.MapDelete("/{orderId:guid}", UnlinkTicketOrder).WithName("UnlinkTicketTdSynnexOrder");
        return app;
    }

    // ---- dashboard -------------------------------------------------------

    /// KPI tiles + an agent-safe projection of the sync health (no error
    /// text, no config — the admin status endpoint has those).
    private static async Task<IResult> GetSummary(
        ITdSynnexOrderRepository repo, ISettingsService settings, CancellationToken ct)
    {
        var today = await TdSynnexOrderEvents.TodayAsync(settings, ct);
        var stats = await repo.GetStatsAsync(today, ct);
        var sync = await repo.GetSyncStateAsync(ct);
        var enabled = await settings.GetAsync<bool>(SettingKeys.TdSynnex.Enabled, ct);
        return Results.Ok(new
        {
            today = today.ToString("yyyy-MM-dd"),
            stats = new
            {
                open = stats.Open,
                investigation = stats.Investigation,
                shipping = stats.Shipping,
                late = stats.Late,
                dueThisWeek = stats.DueThisWeek,
                completedLast30Days = stats.CompletedLast30Days,
                openValue = stats.OpenValue,
                currency = stats.Currency,
            },
            sync = new
            {
                enabled,
                lastSuccessUtc = sync?.LastSuccessUtc,
                healthy = sync?.LastStatus is null or TdSynnexSyncService.StatusOk,
                orderCount = sync?.OrderCount ?? 0,
            },
        });
    }

    private static async Task<IResult> ListOrders(
        string? search, string? bucket, string? sort, string? dir, int? page, int? pageSize,
        ITdSynnexOrderRepository repo, ISettingsService settings, CancellationToken ct)
    {
        var b = bucket is not null && TdSynnexOrderBucket.Known.Contains(bucket) ? bucket : TdSynnexOrderBucket.Active;
        var today = await TdSynnexOrderEvents.TodayAsync(settings, ct);
        var result = await repo.ListAsync(search, b, today, sort, dir, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(r => ToHeaderDto(r, today)),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
            bucket = b,
        });
    }

    private static async Task<IResult> PickOrders(
        string? q, int? limit, ITdSynnexOrderRepository repo, ISettingsService settings, CancellationToken ct)
    {
        var today = await TdSynnexOrderEvents.TodayAsync(settings, ct);
        var rows = await repo.PickAsync(q, limit ?? 10, ct);
        return Results.Ok(new { items = rows.Select(r => ToHeaderDto(r, today)) });
    }

    private static async Task<IResult> GetOrder(
        Guid id, HttpContext http, ITdSynnexOrderRepository repo, IQueueAccessService queueAccess,
        ISettingsService settings, CancellationToken ct)
    {
        var detail = await repo.GetDetailAsync(id, ChangeHistoryOnDetail, ct);
        if (detail is null) return Results.NotFound(new { error = "not_found" });

        var (userId, role) = Caller(http);
        var scope = await queueAccess.GetScopeAsync(userId, role, ct);
        var linked = await repo.GetLinkedTicketsAsync(id, scope, ct);
        var today = await TdSynnexOrderEvents.TodayAsync(settings, ct);
        return Results.Ok(new
        {
            order = ToDetailDto(detail, today),
            linkedTickets = linked.Visible.Select(t => new
            {
                id = t.TicketId,
                number = t.Number,
                subject = t.Subject,
                statusName = t.StatusName,
                statusColor = t.StatusColor,
            }),
            hiddenLinkedTicketCount = linked.HiddenCount,
        });
    }

    /// Runs on the application lifetime, not the request: a long sync must
    /// not be cut off when the proxy times the request out.
    private static async Task<IResult> SyncNow(
        ITdSynnexSyncService sync, ISettingsService settings, IHostApplicationLifetime lifetime, CancellationToken ct)
    {
        if (!await settings.GetAsync<bool>(SettingKeys.TdSynnex.Enabled, ct))
            return Results.Conflict(new { error = "disabled", message = "The TD SYNNEX integration is turned off." });
        var o = await sync.SyncAsync(forceActiveRefresh: false, lifetime.ApplicationStopping);
        // Agents see the outcome, not the upstream error text.
        return Results.Ok(new { success = o.Success, status = o.Status, seen = o.Seen, changed = o.Changed });
    }

    // ---- ticket links ------------------------------------------------------

    private static async Task<IResult> ListTicketOrders(
        Guid ticketId, HttpContext http, ITicketRepository tickets, IQueueAccessService queueAccess,
        ITdSynnexOrderRepository repo, ISettingsService settings, CancellationToken ct)
    {
        var (userId, role) = Caller(http);
        if (await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, ticketId, ct) is null)
            return Results.NotFound(new { error = "not_found" });

        var today = await TdSynnexOrderEvents.TodayAsync(settings, ct);
        var rows = await repo.ListForTicketAsync(ticketId, ChangeHistoryOnTicket, ct);
        return Results.Ok(new { items = rows.Select(d => ToDetailDto(d, today)) });
    }

    private static async Task<IResult> LinkTicketOrder(
        Guid ticketId, Guid orderId, HttpContext http, ITicketRepository tickets, IQueueAccessService queueAccess,
        ITdSynnexOrderRepository repo, ITicketListNotifier notifier, IAuditLogger audit, CancellationToken ct)
    {
        var (userId, role) = Caller(http);
        if (await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, ticketId, ct) is null)
            return Results.NotFound(new { error = "not_found" });

        var order = await repo.GetDetailAsync(orderId, 0, ct);
        if (order is null) return Results.NotFound(new { error = "order_not_found", message = "Order not found." });

        // One ticket covers a handful of orders, not a whole catalogue.
        if (await repo.CountLinksForTicketAsync(ticketId, ct) >= 25)
            return Results.Conflict(new { error = "too_many", message = "A ticket can have at most 25 linked orders." });

        var created = await repo.LinkAsync(ticketId, orderId, userId, ct);
        if (created)
        {
            await LogLinkAsync(http, audit, TdSynnexEventTypes.TicketOrderLinked, ticketId, order.Header.OrderNumber, orderId, ct);
            await notifier.NotifyUpdatedAsync(ticketId, ct);
        }
        return Results.Ok(new { ok = true, created });
    }

    private static async Task<IResult> UnlinkTicketOrder(
        Guid ticketId, Guid orderId, HttpContext http, ITicketRepository tickets, IQueueAccessService queueAccess,
        ITdSynnexOrderRepository repo, ITicketListNotifier notifier, IAuditLogger audit, CancellationToken ct)
    {
        var (userId, role) = Caller(http);
        if (await tickets.GetAccessibleCoreAsync(queueAccess, userId, role, ticketId, ct) is null)
            return Results.NotFound(new { error = "not_found" });

        if (await repo.UnlinkAsync(ticketId, orderId, ct))
        {
            var order = await repo.GetDetailAsync(orderId, 0, ct);
            await LogLinkAsync(http, audit, TdSynnexEventTypes.TicketOrderUnlinked, ticketId, order?.Header.OrderNumber, orderId, ct);
            await notifier.NotifyUpdatedAsync(ticketId, ct);
        }
        return Results.Ok(new { ok = true });
    }

    // ---- mapping -----------------------------------------------------------

    private static object ToHeaderDto(TdSynnexOrderRow r, DateOnly today)
    {
        var first = r.FirstShipDate is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        return new
        {
            id = r.Id,
            orderNumber = r.OrderNumber,
            customerOrderNumber = r.CustomerOrderNumber,
            endUserOrderNumber = r.EndUserOrderNumber,
            orderType = r.OrderType,
            status = r.Status,
            statusDetail = r.StatusDetail,
            createdBy = r.CreatedBy,
            createdUtc = r.OrderCreatedUtc,
            modifiedUtc = r.OrderModifiedUtc,
            shipToName = r.ShipToName,
            shipToCity = r.ShipToCity,
            shipToCountry = r.ShipToCountry,
            totalNet = r.TotalNet,
            currency = r.Currency,
            lineCount = r.LineCount,
            quantityTotal = r.QuantityTotal,
            firstShipDate = first?.ToString("yyyy-MM-dd"),
            etaDate = r.EtaDate is { } e ? DateOnly.FromDateTime(e).ToString("yyyy-MM-dd") : null,
            isLate = TdSynnexOrderEvents.IsLate(r.Status, first, today),
            lastSyncedUtc = r.LastSyncedUtc,
            lastChangedUtc = r.LastChangedUtc,
            linkedTicketCount = r.LinkedTicketCount,
        };
    }

    private static object ToDetailDto(TdSynnexOrderDetail d, DateOnly today) => new
    {
        header = ToHeaderDto(d.Header, today),
        shipTo = d.ShipTo,
        payment = d.Payment,
        lines = d.Lines.Select(l => new
        {
            lineId = l.LineId,
            tdPartNumber = l.TdPartNumber,
            manufacturerPartNumber = l.ManufacturerPartNumber,
            manufacturer = l.Manufacturer,
            description = l.Description,
            isEol = l.IsEol,
            unitNetPrice = l.UnitNetPrice,
            quantity = l.Quantity,
            totalNetPrice = l.TotalNetPrice,
            splits = l.Splits.Select(s => new
            {
                quantity = s.Quantity,
                shipDate = s.ShipDate?.ToString("yyyy-MM-dd"),
                shipDateRaw = s.ShipDateRaw,
                shipDateDetails = s.ShipDateDetails,
                status = s.Status,
                statusDetail = s.StatusDetail,
                isDone = s.IsDone,
                deliveryNotes = s.DeliveryNotes,
                invoices = s.Invoices,
            }),
        }),
        changes = d.Changes.Select(c => new
        {
            id = c.Id,
            detectedUtc = c.DetectedUtc,
            summary = c.Summary,
            items = ParseChanges(c.ChangesJson),
        }),
    };

    private static IReadOnlyList<TdSynnexOrderChange> ParseChanges(string json)
    {
        try { return JsonSerializer.Deserialize<List<TdSynnexOrderChange>>(json, TdSynnexOrderRepository.Json) ?? new(); }
        catch (JsonException) { return Array.Empty<TdSynnexOrderChange>(); }
    }

    private static (Guid UserId, string Role) Caller(HttpContext http) =>
        (ActorContext.GetUserId(http),
         http.User.FindFirst(ClaimTypes.Role)!.Value);

    private static Task LogLinkAsync(
        HttpContext http, IAuditLogger audit, string eventType, Guid ticketId, string? orderNumber, Guid orderId, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: ticketId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { ticketId, orderId, orderNumber }), ct);
    }
}
