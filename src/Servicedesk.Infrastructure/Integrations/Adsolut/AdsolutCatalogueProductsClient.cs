using System.Globalization;
using System.Text;
using System.Text.Json;

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
            nextCursor = TryGetString(paging, "nextCursor");
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
        if (!TryGetGuid(root, "id", out var id)) return null;

        return new AdsolutCatalogueProduct(
            Id: id,
            Code: TryGetString(root, "code"),
            Name: PickTranslation(root, "name"),
            ServiceProduct: TryGetBool(root, "serviceProduct"),
            IsActive: TryGetBool(root, "isActive"),
            Blocked: TryGetBool(root, "blocked"),
            EndOfSeries: TryGetBool(root, "endOfSeries"),
            AdsolutCreatedUtc: TryGetDateTimeOffset(root, "created"),
            AdsolutLastModified: TryGetDateTimeOffset(root, "lastModified"));
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

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static bool TryGetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var prop) &&
        (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False) &&
        prop.GetBoolean();

    /// Adsolut serialises dates offset-less (no 'Z'), e.g. "2026-06-02T11:33:36".
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
