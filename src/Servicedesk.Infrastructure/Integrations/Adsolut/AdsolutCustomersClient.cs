using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutCustomersClient : IAdsolutCustomersClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutCustomersClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public Task<AdsolutPagedResult<AdsolutCustomer>> ListCustomersAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        int page,
        int limit,
        CancellationToken ct = default)
        => ListAsync(administrationId, "customers", AdsolutEventTypes.CustomersList, modifiedSince, page, limit, ct);

    public Task<AdsolutPagedResult<AdsolutCustomer>> ListSuppliersAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        int page,
        int limit,
        CancellationToken ct = default)
        => ListAsync(administrationId, "suppliers", AdsolutEventTypes.SuppliersList, modifiedSince, page, limit, ct);

    private async Task<AdsolutPagedResult<AdsolutCustomer>> ListAsync(
        Guid administrationId,
        string resource,
        string eventType,
        DateTimeOffset? modifiedSince,
        int page,
        int limit,
        CancellationToken ct)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        // Adsolut docs: page is 1-based, limit defaults 50, max 100. Clamp
        // here so a misconfigured caller can't ask for 1M rows in one shot.
        var safePage = Math.Max(1, page);
        var safeLimit = Math.Clamp(limit, 1, 100);

        var query = new StringBuilder();
        query.Append("?Page=").Append(safePage)
             .Append("&Limit=").Append(safeLimit)
             .Append("&OrderBy=").Append(Uri.EscapeDataString("lastModified"));
        if (modifiedSince is { } since)
        {
            // ISO-8601 round-trip (with 'Z') — what Adsolut docs show in
            // examples and what their parser expects. UtcDateTime to drop
            // the offset suffix WK ignores in the OpenAPI examples.
            query.Append("&ModifiedSince=").Append(Uri.EscapeDataString(
                since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }

        // Per the Adsolut OpenAPI spec (saved in repo as Adsolut.openapi.json):
        // server URL is `https://api.adsolut.com/acc/v1`, paths start with
        // `/adm/{administrationId}/...`. The full URL is the concatenation —
        // earlier 404s were missing the `/acc/v1` segment.
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/{resource}{query}";
        return await _invoker.SendAsync(
            eventType: eventType,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseCustomersPage(body);
            },
            auditPayload: new
            {
                administrationId,
                page = safePage,
                limit = safeLimit,
                modifiedSince = modifiedSince?.UtcDateTime,
            },
            ct: ct);
    }

    private static AdsolutPagedResult<AdsolutCustomer> ParseCustomersPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutPagedResult<AdsolutCustomer>(0, 0, 0, Array.Empty<AdsolutCustomer>());
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Defensive parse — accept either the documented `{ items, currentPage, totalPages, totalItems }`
        // shape or a bare array (legacy/test fixtures).
        if (root.ValueKind == JsonValueKind.Array)
        {
            var bare = ParseItems(root);
            return new AdsolutPagedResult<AdsolutCustomer>(1, bare.Count, 1, bare);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutPagedResult<AdsolutCustomer>(0, 0, 0, Array.Empty<AdsolutCustomer>());
        }

        var items = root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array
            ? ParseItems(itemsEl)
            : (IReadOnlyList<AdsolutCustomer>)Array.Empty<AdsolutCustomer>();

        var currentPage = TryGetInt(root, "currentPage", 1);
        var totalItems = TryGetInt(root, "totalItems", items.Count);
        var totalPages = TryGetInt(root, "totalPages", currentPage);
        return new AdsolutPagedResult<AdsolutCustomer>(currentPage, totalItems, totalPages, items);
    }

    private static List<AdsolutCustomer> ParseItems(JsonElement array)
    {
        var list = new List<AdsolutCustomer>(array.GetArrayLength());
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetGuid(el, "id", out var id)) continue;

            // Address: streetName + streetNumber + boxNumber → AddressLine1;
            // boxNumber alone occasionally lands on AddressLine2 (matches
            // the Adsolut UI's "Bus" line).
            var streetName = TryGetString(el, "streetName") ?? string.Empty;
            var streetNumber = TryGetString(el, "streetNumber") ?? string.Empty;
            var boxNumber = TryGetString(el, "boxNumber") ?? string.Empty;
            var line1 = string.IsNullOrEmpty(streetNumber)
                ? streetName
                : $"{streetName} {streetNumber}".Trim();
            var line2 = string.IsNullOrEmpty(boxNumber) ? string.Empty : $"Bus {boxNumber}";

            list.Add(new AdsolutCustomer(
                Id: id,
                Name: TryGetString(el, "name") ?? string.Empty,
                Code: TryGetString(el, "code"),
                Email: TryGetString(el, "email") ?? string.Empty,
                Phone: TryGetString(el, "phone") ?? string.Empty,
                MobilePhone: TryGetString(el, "mobilePhone") ?? string.Empty,
                AddressLine1: line1,
                AddressLine2: line2,
                PostalCode: TryGetString(el, "postalCode") ?? string.Empty,
                City: TryGetString(el, "city") ?? string.Empty,
                Country: TryGetString(el, "country") ?? string.Empty,
                VatNumber: TryGetString(el, "vatNumber") ?? string.Empty,
                CountryPrefixVatNumber: TryGetString(el, "countryPrefixVatNumber") ?? string.Empty,
                LastModified: TryGetDateTimeOffset(el, "lastModified")));
        }
        return list;
    }

    private static int TryGetInt(JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var prop)) return fallback;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)) return value;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    private static bool TryGetGuid(JsonElement el, string name, out Guid value)
    {
        value = Guid.Empty;
        if (!el.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(prop.GetString(), out value);
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Null => null,
            _ => null,
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return null;
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUniversalTime()
            : null;
    }
}
