using System.Globalization;
using System.Text;
using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Http;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutCatalogueProductsClient : IAdsolutCatalogueProductsClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutCatalogueProductsClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutCatalogueProductListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

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

        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/CatalogueProducts{query}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpCatalogueProductsList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseListPage(body);
            },
            auditPayload: new { administrationId, pageSize = safePageSize, modifiedSince = modifiedSince?.UtcDateTime, hasCursor = !string.IsNullOrEmpty(cursor) },
            ct: ct);
    }

    // ---- parsing --------------------------------------------------------

    private static AdsolutCatalogueProductListPage ParseListPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutCatalogueProductListPage(Array.Empty<AdsolutCatalogueProduct>(), null, false);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutCatalogueProductListPage(Array.Empty<AdsolutCatalogueProduct>(), null, false);
        }

        var items = new List<AdsolutCatalogueProduct>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                var product = ParseProduct(el);
                if (product is not null) items.Add(product);
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

        return new AdsolutCatalogueProductListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutCatalogueProduct? ParseProduct(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetGuidProperty("id", out var id)) return null;

        return new AdsolutCatalogueProduct(
            Id: id,
            Code: root.GetStringOrNull("code"),
            Name: PickTranslation(root, "name"),
            ServiceProduct: root.GetBool("serviceProduct"),
            IsActive: root.GetBool("isActive"),
            Blocked: root.GetBool("blocked"),
            EndOfSeries: root.GetBool("endOfSeries"),
            AdsolutCreatedUtc: root.GetDateTimeOffsetUnsetAsNull("created"),
            AdsolutLastModified: root.GetDateTimeOffsetUnsetAsNull("lastModified"));
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
