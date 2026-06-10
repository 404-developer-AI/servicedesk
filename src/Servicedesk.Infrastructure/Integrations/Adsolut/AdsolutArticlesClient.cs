using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutArticlesClient : IAdsolutArticlesClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutArticlesClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutArticleListPage> ListPageAsync(
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

        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/Articles{query}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpArticlesList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseListPage(body);
            },
            auditPayload: new { administrationId, pageSize = safePageSize, modifiedSince = modifiedSince?.UtcDateTime, hasCursor = !string.IsNullOrEmpty(cursor) },
            ct: ct);
    }

    public async Task<AdsolutArticle?> GetByIdAsync(
        Guid administrationId,
        Guid articleId,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/Articles/{articleId}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpArticlesGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return string.IsNullOrWhiteSpace(body) ? null : ParseArticle(JsonDocument.Parse(body).RootElement);
            },
            auditPayload: new { administrationId, articleId },
            ct: ct);
    }

    // ---- parsing --------------------------------------------------------

    private static AdsolutArticleListPage ParseListPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutArticleListPage(Array.Empty<AdsolutArticle>(), null, false);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutArticleListPage(Array.Empty<AdsolutArticle>(), null, false);
        }

        var items = new List<AdsolutArticle>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                var article = ParseArticle(el);
                if (article is not null) items.Add(article);
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

        return new AdsolutArticleListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutArticle? ParseArticle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!TryGetGuid(root, "id", out var id)) return null;

        // vatCode { code, description: [{language, value}], … }
        string? vatCode = null, vatRate = null;
        if (root.TryGetProperty("vatCode", out var vat) && vat.ValueKind == JsonValueKind.Object)
        {
            vatCode = TryGetString(vat, "code");
            vatRate = PickTranslation(vat, "description");
        }

        var active = root.TryGetProperty("active", out var a) &&
                     (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False) &&
                     a.GetBoolean();

        return new AdsolutArticle(
            Id: id,
            Code: TryGetString(root, "code"),
            Name: PickTranslation(root, "name"),
            Description: PickTranslation(root, "description"),
            VatCode: vatCode,
            VatRate: vatRate,
            Active: active,
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

    /// Adsolut serialises dates offset-less (no 'Z'), e.g. "2024-02-21T22:24:49".
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
