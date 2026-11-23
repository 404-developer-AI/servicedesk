using System.Globalization;
using System.Text;
using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Http;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutContractsClient : IAdsolutContractsClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutContractsClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutContractListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        // The Contracts list has NO state/business filters — only cursor +
        // PageSize + ModifiedSince. We mirror every contract and narrow our side.
        var query = new StringBuilder();
        query.Append("?PageSize=").Append(safePageSize);
        if (modifiedSince is { } since)
        {
            query.Append("&ModifiedSince=").Append(Uri.EscapeDataString(
                since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrEmpty(cursor))
        {
            query.Append("&NextCursor=").Append(Uri.EscapeDataString(cursor));
        }

        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/Contracts{query}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpContractsList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseListPage(body);
            },
            auditPayload: new { administrationId, pageSize = safePageSize, modifiedSince = modifiedSince?.UtcDateTime, hasCursor = !string.IsNullOrEmpty(cursor) },
            ct: ct);
    }

    public async Task<AdsolutContract?> GetByIdAsync(
        Guid administrationId,
        Guid contractId,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/Contracts/{contractId}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpContractsGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return string.IsNullOrWhiteSpace(body) ? null : ParseContract(JsonDocument.Parse(body).RootElement);
            },
            auditPayload: new { administrationId, contractId },
            ct: ct);
    }

    // ---- parsing --------------------------------------------------------

    private static AdsolutContractListPage ParseListPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutContractListPage(Array.Empty<AdsolutContract>(), null, false);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutContractListPage(Array.Empty<AdsolutContract>(), null, false);
        }

        var items = new List<AdsolutContract>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                var contract = ParseContract(el);
                if (contract is not null) items.Add(contract);
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

        return new AdsolutContractListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutContract? ParseContract(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetGuidProperty("id", out var id)) return null;

        // contractState { code, state: [{language,value}], … } — note the
        // translation array is named "state" here, not "description".
        string? stateCode = null, stateDescription = null;
        if (root.TryGetProperty("contractState", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            stateCode = state.GetStringOrNull("code");
            stateDescription = PickTranslation(state, "state");
        }

        // periodicity / invoicingPeriodicity { code, term: [{language,value}] }
        string? periodicityCode = null, periodicityLabel = null;
        if (root.TryGetProperty("periodicity", out var per) && per.ValueKind == JsonValueKind.Object)
        {
            periodicityCode = per.GetStringOrNull("code");
            periodicityLabel = PickTranslation(per, "term");
        }

        string? invPeriodicityCode = null, invPeriodicityLabel = null;
        if (root.TryGetProperty("invoicingPeriodicity", out var inv) && inv.ValueKind == JsonValueKind.Object)
        {
            invPeriodicityCode = inv.GetStringOrNull("code");
            invPeriodicityLabel = PickTranslation(inv, "term");
        }

        var lines = ParseLines(root);

        return new AdsolutContract(
            Id: id,
            DocNr: root.GetInt32OrNull("docNr"),
            CustomerAdsolutId: root.TryGetGuidProperty("customerId", out var cid) ? cid : (Guid?)null,
            InvoiceCustomerAdsolutId: root.TryGetGuidProperty("invoiceCustomerId", out var icid) ? icid : (Guid?)null,
            // description = the contract title; name = the customer/company name.
            CustomerName: root.GetStringOrNull("name"),
            StateCode: stateCode,
            StateDescription: stateDescription,
            DocDate: root.GetDateTimeOffsetUnsetAsNull("date"),
            StartDate: root.GetDateTimeOffsetUnsetAsNull("startDate"),
            StopDate: root.GetDateTimeOffsetUnsetAsNull("stopDate"),
            EndDate: root.GetDateTimeOffsetUnsetAsNull("endDate"),
            Description: root.GetStringOrNull("description"),
            Memo: root.GetStringOrNull("memo"),
            PeriodicityCode: periodicityCode,
            PeriodicityLabel: periodicityLabel,
            InvoicingPeriodicityCode: invPeriodicityCode,
            InvoicingPeriodicityLabel: invPeriodicityLabel,
            NumberOfTerms: root.GetInt32OrNull("numberOfTerms"),
            // totalExcl == totalExclIV in every sample; the plain totals are
            // canonical. Stored verbatim (no compute).
            TotalExclVat: root.GetDecimalOrNull("totalExcl") ?? 0m,
            TotalVat: root.GetDecimalOrNull("totalVat") ?? 0m,
            TotalInclVat: root.GetDecimalOrNull("totalIncl") ?? 0m,
            AdsolutCreatedUtc: root.GetDateTimeOffsetUnsetAsNull("created"),
            AdsolutLastModified: root.GetDateTimeOffsetUnsetAsNull("lastModified"),
            Lines: lines);
    }

    private static IReadOnlyList<AdsolutContractLine> ParseLines(JsonElement root)
    {
        if (!root.TryGetProperty("contractDetailArticles", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AdsolutContractLine>();
        }
        var list = new List<AdsolutContractLine>(arr.GetArrayLength());
        var ordinal = 0;
        foreach (var el in arr.EnumerateArray())
        {
            ordinal++;
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetGuidProperty("contractDetailArticleId", out var id)) continue;

            list.Add(new AdsolutContractLine(
                Id: id,
                LineNr: ordinal,
                ArticleId: el.TryGetGuidProperty("articleId", out var aid) ? aid : (Guid?)null,
                Name: el.GetStringOrNull("name"),
                Description: el.GetStringOrNull("description"),
                Quantity: el.GetDecimalOrNull("quantity"),
                GrossUnitPrice: el.GetDecimalOrNull("grossUnitPrice"),
                Discount1: el.GetDecimalOrNull("discount1"),
                Discount2: el.GetDecimalOrNull("discount2"),
                UnitPrice: el.GetDecimalOrNull("unitPrice"),
                UnitPriceIncl: el.GetDecimalOrNull("unitPriceIncl"),
                StartDate: el.GetDateTimeOffsetUnsetAsNull("startDate"),
                EndDate: el.GetDateTimeOffsetUnsetAsNull("endDate")));
        }
        return list;
    }

    /// Pick the Dutch ("Nl") value from a multi-language `[{language, value}]`
    /// array property, falling back to the first entry, then null.
    private static string? PickTranslation(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        string? first = null;
        foreach (var el in arr.EnumerateArray())
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
