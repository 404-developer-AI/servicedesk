using System.Globalization;
using System.Text;
using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Http;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutSalesReceiptsClient : IAdsolutSalesReceiptsClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutSalesReceiptsClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutSalesReceiptListPage> ListPageAsync(
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
             // Invoiced/finished receipts are excluded by default; we want
             // every status in the mirror, so always opt them in.
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

        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/SalesReceiptInfos{query}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpSalesReceiptsList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseListPage(body);
            },
            auditPayload: new { administrationId, pageSize = safePageSize, modifiedSince = modifiedSince?.UtcDateTime, hasCursor = !string.IsNullOrEmpty(cursor) },
            ct: ct);
    }

    public async Task<AdsolutSalesReceipt?> GetByIdAsync(
        Guid administrationId,
        Guid receiptId,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/SalesReceiptInfos/{receiptId}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpSalesReceiptsGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseReceipt(body);
            },
            auditPayload: new { administrationId, receiptId },
            ct: ct);
    }

    // ---- parsing --------------------------------------------------------

    private static AdsolutSalesReceiptListPage ParseListPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutSalesReceiptListPage(Array.Empty<AdsolutSalesReceiptListItem>(), null, false);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutSalesReceiptListPage(Array.Empty<AdsolutSalesReceiptListItem>(), null, false);
        }

        var items = new List<AdsolutSalesReceiptListItem>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetGuidProperty("id", out var id)) continue;
                string? stateCode = null;
                if (el.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                {
                    stateCode = state.GetStringOrNull("code");
                }
                var lastModified = el.GetDateTimeOffsetUnsetAsNull("lastModified");
                items.Add(new AdsolutSalesReceiptListItem(id, stateCode, lastModified));
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

        return new AdsolutSalesReceiptListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutSalesReceipt? ParseReceipt(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetGuidProperty("id", out var id)) return null;

        // customer (rich object with id/code/name)
        Guid? customerId = null;
        string? customerCode = null, customerName = null;
        if (root.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object)
        {
            customerId = cust.TryGetGuidProperty("id", out var cid) ? cid : (Guid?)null;
            customerCode = cust.GetStringOrNull("code");
            customerName = cust.GetStringOrNull("name");
        }

        // state { id, code, description: [{language,value}] }
        Guid? stateId = null;
        string? stateCode = null, stateDescription = null;
        if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            stateId = state.TryGetGuidProperty("id", out var sid) ? sid : (Guid?)null;
            stateCode = state.GetStringOrNull("code");
            stateDescription = PickDescription(state);
        }

        // employee { code, firstName, lastName, email }
        string? employeeCode = null, employeeName = null, employeeEmail = null;
        if (root.TryGetProperty("employee", out var emp) && emp.ValueKind == JsonValueKind.Object)
        {
            employeeCode = emp.GetStringOrNull("code");
            employeeName = JoinName(emp.GetStringOrNull("firstName"), emp.GetStringOrNull("lastName"));
            employeeEmail = emp.GetStringOrNull("email");
        }

        // representative { code, name, email }
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

        string? currencyIso = null;
        if (root.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.Object)
        {
            currencyIso = cur.GetStringOrNull("isoCode");
        }

        var lines = ParseLines(root);
        var performances = ParsePerformances(root);

        return new AdsolutSalesReceipt(
            Id: id,
            DocNr: root.GetInt32OrNull("docNr"),
            BookCode: bookCode,
            CustomerAdsolutId: customerId,
            CustomerCode: customerCode,
            CustomerName: customerName,
            StateId: stateId,
            StateCode: stateCode,
            StateDescription: stateDescription,
            SalesReceiptDate: root.GetDateTimeOffsetUnsetAsNull("salesReceiptDate"),
            Description: root.GetStringOrNull("description"),
            InternalMemo: root.GetStringOrNull("internalMemo"),
            Memo: root.GetStringOrNull("memo"),
            EmployeeCode: employeeCode,
            EmployeeName: employeeName,
            EmployeeEmail: employeeEmail,
            RepresentativeCode: representativeCode,
            RepresentativeName: representativeName,
            CurrencyIso: currencyIso,
            VatIncluded: root.TryGetProperty("vatIncluded", out var vi) && vi.ValueKind == JsonValueKind.True,
            AdsolutCreatedUtc: root.GetDateTimeOffsetUnsetAsNull("created"),
            AdsolutLastModified: root.GetDateTimeOffsetUnsetAsNull("lastModified"),
            Lines: lines,
            Performances: performances);
    }

    private static IReadOnlyList<AdsolutSalesReceiptLine> ParseLines(JsonElement root)
    {
        if (!root.TryGetProperty("salesReceiptInfoDetails", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AdsolutSalesReceiptLine>();
        }
        var list = new List<AdsolutSalesReceiptLine>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetGuidProperty("id", out var id)) continue;
            list.Add(new AdsolutSalesReceiptLine(
                Id: id,
                LineNr: el.GetInt32OrNull("lineNr"),
                ProductCode: el.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object ? p.GetStringOrNull("code") : null,
                Name: el.GetStringOrNull("name"),
                Description: el.GetStringOrNull("description"),
                Quantity: el.GetDecimalOrNull("quantity"),
                UnitCode: el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Object ? u.GetStringOrNull("code") : null,
                UnitPrice: el.GetDecimalOrNull("unitPrice"),
                TotalExclVat: el.GetDecimalOrNull("totalPriceExclVat"),
                TotalInclVat: el.GetDecimalOrNull("totalPriceInclVat"),
                VatCode: el.TryGetProperty("vatCode", out var v) && v.ValueKind == JsonValueKind.Object ? v.GetStringOrNull("code") : null));
        }
        return list;
    }

    private static IReadOnlyList<AdsolutSalesReceiptPerformance> ParsePerformances(JsonElement root)
    {
        if (!root.TryGetProperty("salesReceiptInfoPerformances", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AdsolutSalesReceiptPerformance>();
        }
        var list = new List<AdsolutSalesReceiptPerformance>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetGuidProperty("id", out var id)) continue;
            list.Add(new AdsolutSalesReceiptPerformance(
                Id: id,
                EmployeeCode: el.TryGetProperty("employee", out var e) && e.ValueKind == JsonValueKind.Object ? e.GetStringOrNull("code") : null,
                PerformanceDate: el.GetDateTimeOffsetUnsetAsNull("date"),
                FromTime: el.GetStringOrNull("from"),
                UntilTime: el.GetStringOrNull("until"),
                DurationMinutes: el.GetDecimalOrNull("duration"),
                InvoiceDurationMinutes: el.GetDecimalOrNull("invoiceDuration"),
                InvoiceUnitPrice: el.GetDecimalOrNull("invoiceUnitprice"),
                InvoiceTotal: el.GetDecimalOrNull("invoiceTotal"),
                PerformanceCode: el.TryGetProperty("performanceCode", out var pc) && pc.ValueKind == JsonValueKind.Object ? pc.GetStringOrNull("code") : null,
                Description: el.GetStringOrNull("description")));
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

    private static string? JoinName(string? first, string? last)
    {
        var f = (first ?? string.Empty).Trim();
        var l = (last ?? string.Empty).Trim();
        var joined = $"{f} {l}".Trim();
        return joined.Length == 0 ? null : joined;
    }

}
