using System.Globalization;
using System.Text;
using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Http;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutOrdersClient : IAdsolutOrdersClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutOrdersClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutOrderListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        var query = new StringBuilder();
        query.Append("?PageSize=").Append(safePageSize)
             // Finished/closed orders are excluded by default; we mirror every
             // status (the filter is display-only), so always opt them in.
             .Append("&IncludeFinishedState=true");
        if (modifiedSince is { } since)
        {
            query.Append("&ModifiedSince=").Append(Uri.EscapeDataString(
                since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrEmpty(cursor))
        {
            query.Append("&NextCursor=").Append(Uri.EscapeDataString(cursor));
        }

        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/OrderInfos{query}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpOrdersList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseListPage(body);
            },
            auditPayload: new { administrationId, pageSize = safePageSize, modifiedSince = modifiedSince?.UtcDateTime, hasCursor = !string.IsNullOrEmpty(cursor) },
            ct: ct);
    }

    public async Task<AdsolutOrder?> GetByIdAsync(
        Guid administrationId,
        Guid orderId,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/OrderInfos/{orderId}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpOrdersGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return string.IsNullOrWhiteSpace(body) ? null : ParseOrder(JsonDocument.Parse(body).RootElement);
            },
            auditPayload: new { administrationId, orderId },
            ct: ct);
    }

    // ---- parsing --------------------------------------------------------

    private static AdsolutOrderListPage ParseListPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutOrderListPage(Array.Empty<AdsolutOrder>(), null, false);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutOrderListPage(Array.Empty<AdsolutOrder>(), null, false);
        }

        var items = new List<AdsolutOrder>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                var order = ParseOrder(el);
                if (order is not null) items.Add(order);
            }
        }

        string? nextCursor = null;
        var hasNext = false;
        if (root.TryGetProperty("pagingData", out var paging) && paging.ValueKind == JsonValueKind.Object)
        {
            nextCursor = paging.GetStringOrNull("nextCursor");
            if (paging.TryGetProperty("hasNext", out var hn) &&
                (hn.ValueKind == JsonValueKind.True || hn.ValueKind == JsonValueKind.False))
            {
                hasNext = hn.GetBoolean();
            }
        }

        return new AdsolutOrderListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutOrder? ParseOrder(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetGuidProperty("id", out var id)) return null;

        // customer (rich object with id/code/name); fall back to the top-level
        // "name" mirror that the header also carries.
        Guid? customerId = null;
        string? customerCode = null, customerName = null;
        if (root.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object)
        {
            customerId = cust.TryGetGuidProperty("id", out var cid) ? cid : (Guid?)null;
            customerCode = cust.GetStringOrNull("code");
            customerName = cust.GetStringOrNull("name");
        }
        customerName ??= root.GetStringOrNull("name");

        // orderState { id, code, description: [{language,value}] }
        Guid? stateId = null;
        string? stateCode = null, stateDescription = null;
        if (root.TryGetProperty("orderState", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            stateId = state.TryGetGuidProperty("id", out var sid) ? sid : (Guid?)null;
            stateCode = state.GetStringOrNull("code");
            stateDescription = PickDescription(state);
        }

        string? representativeCode = null, representativeName = null;
        if (root.TryGetProperty("representative", out var rep) && rep.ValueKind == JsonValueKind.Object)
        {
            representativeCode = rep.GetStringOrNull("code");
            representativeName = rep.GetStringOrNull("name");
        }

        string? bookCode = null;
        if (root.TryGetProperty("book", out var book) && book.ValueKind == JsonValueKind.Object)
        {
            bookCode = book.GetStringOrNull("code");
        }

        var lines = ParseLines(root);

        return new AdsolutOrder(
            Id: id,
            DocNr: root.GetInt32OrNull("docNr"),
            BookCode: bookCode,
            KluwerRef: root.GetStringOrNull("kluwerRef"),
            CustomerAdsolutId: customerId,
            CustomerCode: customerCode,
            CustomerName: customerName,
            StateId: stateId,
            StateCode: stateCode,
            StateDescription: stateDescription,
            OrderDate: root.GetDateTimeOffsetUnsetAsNull("orderDate"),
            RequestedDeliveryDate: root.GetDateTimeOffsetUnsetAsNull("requestedDeliveryDate"),
            ConfirmedDeliveryDate: root.GetDateTimeOffsetUnsetAsNull("confirmedDeliveryDate"),
            Remark: root.GetStringOrNull("remark"),
            InternalMemo: root.GetStringOrNull("internalMemo"),
            RepresentativeCode: representativeCode,
            RepresentativeName: representativeName,
            // currency is a plain ISO string ("EUR") on orders, not an object.
            CurrencyIso: root.GetStringOrNull("currency"),
            TotalExclVat: root.GetDecimalOrNull("totalPriceExclVat") ?? 0m,
            TotalInclVat: root.GetDecimalOrNull("totalPriceInclVat") ?? 0m,
            AdsolutCreatedUtc: root.GetDateTimeOffsetUnsetAsNull("created"),
            AdsolutLastModified: root.GetDateTimeOffsetUnsetAsNull("lastModified"),
            Lines: lines);
    }

    private static IReadOnlyList<AdsolutOrderLine> ParseLines(JsonElement root)
    {
        if (!root.TryGetProperty("orderLines", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AdsolutOrderLine>();
        }
        var list = new List<AdsolutOrderLine>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetGuidProperty("orderInfoDetailId", out var id)) continue;

            string? unitCode = null, unitDescription = null;
            if (el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                unitCode = u.GetStringOrNull("code");
                unitDescription = PickDescription(u);
            }

            string? vatCode = null, vatDescription = null;
            if (el.TryGetProperty("vatCode", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                vatCode = v.GetStringOrNull("code");
                vatDescription = PickDescription(v);
            }

            string? lineStateCode = null, lineStateDescription = null;
            if (el.TryGetProperty("orderDetailState", out var ls) && ls.ValueKind == JsonValueKind.Object)
            {
                lineStateCode = ls.GetStringOrNull("code");
                lineStateDescription = PickDescription(ls);
            }

            list.Add(new AdsolutOrderLine(
                Id: id,
                LineNr: el.GetInt32OrNull("lineNr"),
                ProductId: el.TryGetGuidProperty("productId", out var pid) ? pid : (Guid?)null,
                ProductCode: el.GetStringOrNull("productNr"),
                Description: el.GetStringOrNull("description"),
                Quantity: el.GetDecimalOrNull("quantity"),
                UnitCode: unitCode,
                UnitDescription: unitDescription,
                Delivered: el.GetDecimalOrNull("delivered"),
                GrossUnitPrice: el.GetDecimalOrNull("grossUnitPrice"),
                UnitPrice: el.GetDecimalOrNull("unitPrice"),
                Discount1: el.GetDecimalOrNull("discount1"),
                Discount2: el.GetDecimalOrNull("discount2"),
                PriceExclVat: el.GetDecimalOrNull("priceExclVat"),
                PriceInclVat: el.GetDecimalOrNull("priceInclVat"),
                VatCode: vatCode,
                VatDescription: vatDescription,
                StateCode: lineStateCode,
                StateDescription: lineStateDescription,
                RequestedDeliveryDate: el.GetDateTimeOffsetUnsetAsNull("requestedDeliveryDate"),
                ConfirmedDeliveryDate: el.GetDateTimeOffsetUnsetAsNull("confirmedDeliveryDate")));
        }
        return list;
    }

    /// Pick the Dutch ("Nl") value from a `description: [{language, value}]`
    /// array, falling back to the first entry, then null.
    private static string? PickDescription(JsonElement obj)
    {
        if (!obj.TryGetProperty("description", out var desc) || desc.ValueKind != JsonValueKind.Array) return null;
        string? first = null;
        foreach (var el in desc.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var value = el.GetStringOrNull("value");
            if (value is null) continue;
            first ??= value;
            if (string.Equals(el.GetStringOrNull("language"), "Nl", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return first;
    }

}
