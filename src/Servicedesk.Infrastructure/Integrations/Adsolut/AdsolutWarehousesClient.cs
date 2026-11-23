using System.Text;
using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Http;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutWarehousesClient : IAdsolutWarehousesClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutWarehousesClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutWarehouseListPage> ListPageAsync(
        Guid administrationId,
        string? cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        var query = new StringBuilder();
        query.Append("?PageSize=").Append(safePageSize);
        if (!string.IsNullOrEmpty(cursor))
        {
            query.Append("&NextCursor=").Append(Uri.EscapeDataString(cursor));
        }

        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/Warehouses{query}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpWarehousesList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseListPage(body);
            },
            auditPayload: new { administrationId, pageSize = safePageSize, hasCursor = !string.IsNullOrEmpty(cursor) },
            ct: ct);
    }

    private static AdsolutWarehouseListPage ParseListPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutWarehouseListPage(Array.Empty<AdsolutWarehouse>(), null, false);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AdsolutWarehouseListPage(Array.Empty<AdsolutWarehouse>(), null, false);
        }

        var items = new List<AdsolutWarehouse>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                var wh = ParseWarehouse(el);
                if (wh is not null) items.Add(wh);
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

        return new AdsolutWarehouseListPage(items, string.IsNullOrEmpty(nextCursor) ? null : nextCursor, hasNext);
    }

    private static AdsolutWarehouse? ParseWarehouse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetGuidProperty("id", out var id)) return null;

        var locations = new List<AdsolutWarehouseLocation>();
        if (root.TryGetProperty("warehouseLocations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetGuidProperty("id", out var lid)) continue;
                locations.Add(new AdsolutWarehouseLocation(
                    Id: lid,
                    Name: el.GetStringOrNull("name"),
                    IsDefault: el.GetBool("default")));
            }
        }

        return new AdsolutWarehouse(
            Id: id,
            Code: root.GetStringOrNull("code"),
            Name: root.GetStringOrNull("name"),
            Active: root.GetBool("active"),
            Standard: root.GetBool("standard"),
            Locations: locations);
    }

}
