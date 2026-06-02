using System.Globalization;
using System.Text;
using System.Text.Json;

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
                if (!TryGetGuid(el, "id", out var id)) continue;
                string? stateCode = null;
                if (el.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                {
                    stateCode = TryGetString(state, "code");
                }
                var lastModified = TryGetDateTimeOffset(el, "lastModified");
                items.Add(new AdsolutSalesReceiptListItem(id, stateCode, lastModified));
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

        return new AdsolutSalesReceiptListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutSalesReceipt? ParseReceipt(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!TryGetGuid(root, "id", out var id)) return null;

        // customer (rich object with id/code/name)
        Guid? customerId = null;
        string? customerCode = null, customerName = null;
        if (root.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object)
        {
            customerId = TryGetGuid(cust, "id", out var cid) ? cid : (Guid?)null;
            customerCode = TryGetString(cust, "code");
            customerName = TryGetString(cust, "name");
        }

        // state { id, code, description: [{language,value}] }
        Guid? stateId = null;
        string? stateCode = null, stateDescription = null;
        if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            stateId = TryGetGuid(state, "id", out var sid) ? sid : (Guid?)null;
            stateCode = TryGetString(state, "code");
            stateDescription = PickDescription(state);
        }

        // employee { code, firstName, lastName, email }
        string? employeeCode = null, employeeName = null, employeeEmail = null;
        if (root.TryGetProperty("employee", out var emp) && emp.ValueKind == JsonValueKind.Object)
        {
            employeeCode = TryGetString(emp, "code");
            employeeName = JoinName(TryGetString(emp, "firstName"), TryGetString(emp, "lastName"));
            employeeEmail = TryGetString(emp, "email");
        }

        // representative { code, name, email }
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

        string? currencyIso = null;
        if (root.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.Object)
        {
            currencyIso = TryGetString(cur, "isoCode");
        }

        var lines = ParseLines(root);
        var performances = ParsePerformances(root);

        return new AdsolutSalesReceipt(
            Id: id,
            DocNr: TryGetInt(root, "docNr"),
            BookCode: bookCode,
            CustomerAdsolutId: customerId,
            CustomerCode: customerCode,
            CustomerName: customerName,
            StateId: stateId,
            StateCode: stateCode,
            StateDescription: stateDescription,
            SalesReceiptDate: TryGetDateTimeOffset(root, "salesReceiptDate"),
            Description: TryGetString(root, "description"),
            InternalMemo: TryGetString(root, "internalMemo"),
            Memo: TryGetString(root, "memo"),
            EmployeeCode: employeeCode,
            EmployeeName: employeeName,
            EmployeeEmail: employeeEmail,
            RepresentativeCode: representativeCode,
            RepresentativeName: representativeName,
            CurrencyIso: currencyIso,
            VatIncluded: root.TryGetProperty("vatIncluded", out var vi) && vi.ValueKind == JsonValueKind.True,
            AdsolutCreatedUtc: TryGetDateTimeOffset(root, "created"),
            AdsolutLastModified: TryGetDateTimeOffset(root, "lastModified"),
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
            if (!TryGetGuid(el, "id", out var id)) continue;
            list.Add(new AdsolutSalesReceiptLine(
                Id: id,
                LineNr: TryGetInt(el, "lineNr"),
                ProductCode: el.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object ? TryGetString(p, "code") : null,
                Name: TryGetString(el, "name"),
                Description: TryGetString(el, "description"),
                Quantity: TryGetDecimal(el, "quantity"),
                UnitCode: el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Object ? TryGetString(u, "code") : null,
                UnitPrice: TryGetDecimal(el, "unitPrice"),
                TotalExclVat: TryGetDecimal(el, "totalPriceExclVat"),
                TotalInclVat: TryGetDecimal(el, "totalPriceInclVat"),
                VatCode: el.TryGetProperty("vatCode", out var v) && v.ValueKind == JsonValueKind.Object ? TryGetString(v, "code") : null));
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
            if (!TryGetGuid(el, "id", out var id)) continue;
            list.Add(new AdsolutSalesReceiptPerformance(
                Id: id,
                EmployeeCode: el.TryGetProperty("employee", out var e) && e.ValueKind == JsonValueKind.Object ? TryGetString(e, "code") : null,
                PerformanceDate: TryGetDateTimeOffset(el, "date"),
                FromTime: TryGetString(el, "from"),
                UntilTime: TryGetString(el, "until"),
                DurationMinutes: TryGetDecimal(el, "duration"),
                InvoiceDurationMinutes: TryGetDecimal(el, "invoiceDuration"),
                InvoiceUnitPrice: TryGetDecimal(el, "invoiceUnitprice"),
                InvoiceTotal: TryGetDecimal(el, "invoiceTotal"),
                PerformanceCode: el.TryGetProperty("performanceCode", out var pc) && pc.ValueKind == JsonValueKind.Object ? TryGetString(pc, "code") : null,
                Description: TryGetString(el, "description")));
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

    private static string? JoinName(string? first, string? last)
    {
        var f = (first ?? string.Empty).Trim();
        var l = (last ?? string.Empty).Trim();
        var joined = $"{f} {l}".Trim();
        return joined.Length == 0 ? null : joined;
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

    /// Adsolut serialises dates offset-less (no 'Z'), e.g. "2020-01-01T00:00:00".
    /// Parse as UTC (AssumeUniversal). The sentinel "0001-01-01T00:00:00"
    /// means "unset" → null, so a default date never lands in the mirror.
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
