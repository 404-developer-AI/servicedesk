using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutSupplierOrdersClient : IAdsolutSupplierOrdersClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutSupplierOrdersClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutSupplierOrderListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        var query = new StringBuilder();
        // SupplierOrderInfos has NO IncludeFinishedState param (that belongs to
        // OrderInfos/SalesReceiptInfos). Its real status filter is
        // IncludeReceivedState: absent/false returns ONLY not-received orders,
        // silently dropping anything in state "ontv" (ontvangen). We mirror every
        // status, so this MUST be true — otherwise a bestelling that gets received
        // falls out of the delta and the mirror stays stuck on its last-seen
        // status/delivered (e.g. OPEN / delivered 0 forever).
        query.Append("?PageSize=").Append(safePageSize)
             .Append("&IncludeReceivedState=true");
        if (modifiedSince is { } since)
        {
            query.Append("&ModifiedSince=").Append(Uri.EscapeDataString(
                since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrEmpty(cursor))
        {
            query.Append("&NextCursor=").Append(Uri.EscapeDataString(cursor));
        }

        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/SupplierOrderInfos{query}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpSupplierOrdersList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseListPage(body);
            },
            auditPayload: new { administrationId, pageSize = safePageSize, modifiedSince = modifiedSince?.UtcDateTime, hasCursor = !string.IsNullOrEmpty(cursor) },
            ct: ct);
    }

    private static AdsolutSupplierOrderListPage ParseListPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutSupplierOrderListPage(Array.Empty<AdsolutSupplierOrder>(), null, false);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutSupplierOrderListPage(Array.Empty<AdsolutSupplierOrder>(), null, false);
        }

        var items = new List<AdsolutSupplierOrder>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                var order = ParseSupplierOrder(el);
                if (order is not null) items.Add(order);
            }
        }

        string? nextCursor = null;
        var hasNext = false;
        if (root.TryGetProperty("pagingData", out var paging) && paging.ValueKind == JsonValueKind.Object)
        {
            nextCursor = TryGetString(paging, "nextCursor");
            if (paging.TryGetProperty("hasNext", out var hn) &&
                (hn.ValueKind == JsonValueKind.True || hn.ValueKind == JsonValueKind.False))
            {
                hasNext = hn.GetBoolean();
            }
        }

        return new AdsolutSupplierOrderListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutSupplierOrder? ParseSupplierOrder(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!TryGetGuid(root, "id", out var id)) return null;

        Guid? supplierId = null;
        string? supplierCode = null, supplierName = null;
        if (root.TryGetProperty("supplier", out var sup) && sup.ValueKind == JsonValueKind.Object)
        {
            supplierId = TryGetGuid(sup, "id", out var sid) ? sid : (Guid?)null;
            supplierCode = TryGetString(sup, "code");
            supplierName = TryGetString(sup, "name");
        }

        string? bookCode = null;
        if (root.TryGetProperty("book", out var book) && book.ValueKind == JsonValueKind.Object)
        {
            bookCode = TryGetString(book, "code");
        }

        string? headerStateCode = null;
        if (root.TryGetProperty("supplierOrderState", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            headerStateCode = TryGetString(st, "code");
        }

        return new AdsolutSupplierOrder(
            Id: id,
            DocNr: TryGetInt(root, "docNr"),
            BookCode: bookCode,
            SupplierId: supplierId,
            SupplierCode: supplierCode,
            SupplierName: supplierName,
            StateCode: headerStateCode,
            SupplierOrderDate: TryGetDateTimeOffset(root, "supplierOrderDate"),
            AdsolutLastModified: TryGetDateTimeOffset(root, "lastModified"),
            Lines: ParseLines(root));
    }

    private static IReadOnlyList<AdsolutSupplierOrderLine> ParseLines(JsonElement root)
    {
        if (!root.TryGetProperty("supplierOrderLines", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AdsolutSupplierOrderLine>();
        }
        var list = new List<AdsolutSupplierOrderLine>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetGuid(el, "supplierOrderInfoDetailId", out var id)) continue;

            Guid? productId = null;
            string? productCode = null;
            if (el.TryGetProperty("product", out var prod) && prod.ValueKind == JsonValueKind.Object)
            {
                productId = TryGetGuid(prod, "id", out var pid) ? pid : (Guid?)null;
                productCode = TryGetString(prod, "code");
            }

            string? unitCode = null;
            if (el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                unitCode = TryGetString(u, "code");
            }

            string? statusCode = null;
            if (el.TryGetProperty("supplierOrderDetailState", out var ds) && ds.ValueKind == JsonValueKind.Object)
            {
                statusCode = TryGetString(ds, "code");
            }

            // warehouse {id, code} = "Stock"; warehouseLocation {id, code} =
            // "Location". The line only carries id + code — the display name is
            // resolved at read time against the Warehouses mirror.
            Guid? warehouseId = null;
            string? warehouseCode = null;
            if (el.TryGetProperty("warehouse", out var wh) && wh.ValueKind == JsonValueKind.Object)
            {
                warehouseId = TryGetGuid(wh, "id", out var whid) ? whid : (Guid?)null;
                warehouseCode = TryGetString(wh, "code");
            }

            Guid? warehouseLocationId = null;
            string? warehouseLocationCode = null;
            if (el.TryGetProperty("warehouseLocation", out var wl) && wl.ValueKind == JsonValueKind.Object)
            {
                warehouseLocationId = TryGetGuid(wl, "id", out var wlid) ? wlid : (Guid?)null;
                warehouseLocationCode = TryGetString(wl, "code");
            }

            // Link back to the sales order (header). The nested object is named
            // orderInfoDetail but carries only orderInfoId (+ docNr) — no order
            // LINE id.
            Guid? linkedOrderId = null;
            int? linkedOrderDocNr = null;
            if (el.TryGetProperty("orderInfoDetail", out var od) && od.ValueKind == JsonValueKind.Object)
            {
                linkedOrderId = TryGetGuid(od, "orderInfoId", out var oid) ? oid : (Guid?)null;
                linkedOrderDocNr = TryGetInt(od, "docNr");
            }

            list.Add(new AdsolutSupplierOrderLine(
                Id: id,
                LineNr: TryGetInt(el, "lineNr"),
                ProductId: productId,
                ProductCode: productCode,
                Name: TryGetString(el, "name"),
                Description: TryGetString(el, "description"),
                Quantity: TryGetDecimal(el, "quantity"),
                Delivered: TryGetDecimal(el, "delivered"),
                UnitCode: unitCode,
                GrossUnitPrice: TryGetDecimal(el, "grossUnitPrice"),
                UnitPrice: TryGetDecimal(el, "unitPrice"),
                Discount1: TryGetDecimal(el, "discount1"),
                StatusCode: statusCode,
                WarehouseId: warehouseId,
                WarehouseCode: warehouseCode,
                WarehouseLocationId: warehouseLocationId,
                WarehouseLocationCode: warehouseLocationCode,
                LinkedOrderId: linkedOrderId,
                LinkedOrderDocNr: linkedOrderDocNr));
        }
        return list;
    }

    private static bool TryGetGuid(JsonElement el, string name, out Guid value)
    {
        value = Guid.Empty;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(prop.GetString(), out value);
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v)) return v;
        return null;
    }

    private static decimal? TryGetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var v)) return v;
        return null;
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return null;
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.StartsWith("0001-01-01", StringComparison.Ordinal)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUniversalTime()
            : null;
    }
}
