using System.Globalization;
using System.Text;
using System.Text.Json;

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
            nextCursor = TryGetString(paging, "nextCursor");
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
        if (!TryGetGuid(root, "id", out var id)) return null;

        // customer (rich object with id/code/name); fall back to the top-level
        // "name" mirror that the header also carries.
        Guid? customerId = null;
        string? customerCode = null, customerName = null;
        if (root.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object)
        {
            customerId = TryGetGuid(cust, "id", out var cid) ? cid : (Guid?)null;
            customerCode = TryGetString(cust, "code");
            customerName = TryGetString(cust, "name");
        }
        customerName ??= TryGetString(root, "name");

        // orderState { id, code, description: [{language,value}] }
        Guid? stateId = null;
        string? stateCode = null, stateDescription = null;
        if (root.TryGetProperty("orderState", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            stateId = TryGetGuid(state, "id", out var sid) ? sid : (Guid?)null;
            stateCode = TryGetString(state, "code");
            stateDescription = PickDescription(state);
        }

        string? representativeCode = null, representativeName = null;
        if (root.TryGetProperty("representative", out var rep) && rep.ValueKind == JsonValueKind.Object)
        {
            representativeCode = TryGetString(rep, "code");
            representativeName = TryGetString(rep, "name");
        }

        string? bookCode = null;
        if (root.TryGetProperty("book", out var book) && book.ValueKind == JsonValueKind.Object)
        {
            bookCode = TryGetString(book, "code");
        }

        var lines = ParseLines(root);

        return new AdsolutOrder(
            Id: id,
            DocNr: TryGetInt(root, "docNr"),
            BookCode: bookCode,
            KluwerRef: TryGetString(root, "kluwerRef"),
            CustomerAdsolutId: customerId,
            CustomerCode: customerCode,
            CustomerName: customerName,
            StateId: stateId,
            StateCode: stateCode,
            StateDescription: stateDescription,
            OrderDate: TryGetDateTimeOffset(root, "orderDate"),
            RequestedDeliveryDate: TryGetDateTimeOffset(root, "requestedDeliveryDate"),
            ConfirmedDeliveryDate: TryGetDateTimeOffset(root, "confirmedDeliveryDate"),
            Remark: TryGetString(root, "remark"),
            InternalMemo: TryGetString(root, "internalMemo"),
            RepresentativeCode: representativeCode,
            RepresentativeName: representativeName,
            // currency is a plain ISO string ("EUR") on orders, not an object.
            CurrencyIso: TryGetString(root, "currency"),
            TotalExclVat: TryGetDecimal(root, "totalPriceExclVat") ?? 0m,
            TotalInclVat: TryGetDecimal(root, "totalPriceInclVat") ?? 0m,
            AdsolutCreatedUtc: TryGetDateTimeOffset(root, "created"),
            AdsolutLastModified: TryGetDateTimeOffset(root, "lastModified"),
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
            if (!TryGetGuid(el, "orderInfoDetailId", out var id)) continue;

            string? unitCode = null, unitDescription = null;
            if (el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                unitCode = TryGetString(u, "code");
                unitDescription = PickDescription(u);
            }

            string? vatCode = null, vatDescription = null;
            if (el.TryGetProperty("vatCode", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                vatCode = TryGetString(v, "code");
                vatDescription = PickDescription(v);
            }

            string? lineStateCode = null, lineStateDescription = null;
            if (el.TryGetProperty("orderDetailState", out var ls) && ls.ValueKind == JsonValueKind.Object)
            {
                lineStateCode = TryGetString(ls, "code");
                lineStateDescription = PickDescription(ls);
            }

            list.Add(new AdsolutOrderLine(
                Id: id,
                LineNr: TryGetInt(el, "lineNr"),
                ProductId: TryGetGuid(el, "productId", out var pid) ? pid : (Guid?)null,
                ProductCode: TryGetString(el, "productNr"),
                Description: TryGetString(el, "description"),
                Quantity: TryGetDecimal(el, "quantity"),
                UnitCode: unitCode,
                UnitDescription: unitDescription,
                Delivered: TryGetDecimal(el, "delivered"),
                GrossUnitPrice: TryGetDecimal(el, "grossUnitPrice"),
                UnitPrice: TryGetDecimal(el, "unitPrice"),
                Discount1: TryGetDecimal(el, "discount1"),
                Discount2: TryGetDecimal(el, "discount2"),
                PriceExclVat: TryGetDecimal(el, "priceExclVat"),
                PriceInclVat: TryGetDecimal(el, "priceInclVat"),
                VatCode: vatCode,
                VatDescription: vatDescription,
                StateCode: lineStateCode,
                StateDescription: lineStateDescription,
                RequestedDeliveryDate: TryGetDateTimeOffset(el, "requestedDeliveryDate"),
                ConfirmedDeliveryDate: TryGetDateTimeOffset(el, "confirmedDeliveryDate")));
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
            var value = TryGetString(el, "value");
            if (value is null) continue;
            first ??= value;
            if (string.Equals(TryGetString(el, "language"), "Nl", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return first;
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

    /// Adsolut serialises dates offset-less (no 'Z'), e.g. "2026-06-04T00:00:00".
    /// Parse as UTC (AssumeUniversal). The sentinel "0001-01-01T00:00:00" means
    /// "unset" → null, so a default date never lands in the mirror.
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
