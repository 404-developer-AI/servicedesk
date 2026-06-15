using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One ERP customer (relation) resolved from
/// GET /erp/v1/adm/{adm}/Customers/{id}. The ERP customer GUID differs from the
/// Accounting company GUID, but the relation <see cref="Code"/> is shared — it
/// equals companies.adsolut_number, which is how ERP documents (contracts,
/// orders, sales-receipts) bridge to the local companies mirror.
public sealed record AdsolutErpCustomer(Guid Id, string? Code, string? Name);

public interface IAdsolutErpCustomersClient
{
    /// Fetch one ERP customer by its ERP GUID. Returns null when the upstream
    /// body is empty/non-object; throws <see cref="AdsolutApiException"/> on a
    /// non-success HTTP status (the caller handles 429 / continues on others).
    Task<AdsolutErpCustomer?> GetByIdAsync(Guid administrationId, Guid customerId, CancellationToken ct = default);
}

public sealed class AdsolutErpCustomersClient : IAdsolutErpCustomersClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutErpCustomersClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<AdsolutErpCustomer?> GetByIdAsync(
        Guid administrationId, Guid customerId, CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/erp/v1/adm/{administrationId}/Customers/{customerId}";
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.ErpCustomersGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                if (string.IsNullOrWhiteSpace(body)) return null;
                var root = JsonDocument.Parse(body).RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                var id = TryGetGuid(root, "id", out var rid) ? rid : customerId;
                return new AdsolutErpCustomer(id, TryGetString(root, "code"), TryGetString(root, "name"));
            },
            auditPayload: new { administrationId, customerId },
            ct: ct);
    }

    private static bool TryGetGuid(JsonElement el, string name, out Guid value)
    {
        value = Guid.Empty;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(prop.GetString(), out value);
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return null;
        var s = prop.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
