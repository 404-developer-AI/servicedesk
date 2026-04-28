using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutAdministrationsClient : IAdsolutAdministrationsClient
{
    private readonly AdsolutHttpInvoker _invoker;

    public AdsolutAdministrationsClient(AdsolutHttpInvoker invoker)
    {
        _invoker = invoker;
    }

    public async Task<IReadOnlyList<AdsolutAdministrationSummary>> ListAsync(CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        return await _invoker.SendAsync(
            eventType: AdsolutEventTypes.AdministrationsList,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/adm/v1/administrations"),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseAdministrations(body);
            },
            ct: ct);
    }

    public async Task ActivateAsync(Guid administrationId, CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        await _invoker.SendAsync(
            eventType: AdsolutEventTypes.AdministrationsActivate,
            buildRequest: () => new HttpRequestMessage(
                HttpMethod.Post,
                $"{baseUrl}/adm/v1/administrations/{administrationId}/integrations"),
            parseSuccess: (_, _) => Task.FromResult<object?>(null),
            auditPayload: new { administrationId },
            ct: ct);
    }

    public async Task DeactivateAsync(Guid administrationId, CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        await _invoker.SendAsync(
            eventType: AdsolutEventTypes.AdministrationsDeactivate,
            buildRequest: () => new HttpRequestMessage(
                HttpMethod.Delete,
                $"{baseUrl}/adm/v1/administrations/{administrationId}/integrations"),
            parseSuccess: (_, _) => Task.FromResult<object?>(null),
            auditPayload: new { administrationId },
            ct: ct);
    }

    private static IReadOnlyList<AdsolutAdministrationSummary> ParseAdministrations(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<AdsolutAdministrationSummary>();

        // The /adm/v1/administrations response is documented as either a bare
        // JSON array or the standard paged-result shape `{ items: [...] }`.
        // The auth-portal export listed the latter; defensive parser handles
        // both so a future shape change at WK doesn't take the picker down.
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var itemsEl))
        {
            items = itemsEl;
        }
        else
        {
            return Array.Empty<AdsolutAdministrationSummary>();
        }

        var result = new List<AdsolutAdministrationSummary>(items.GetArrayLength());
        foreach (var el in items.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetGuid(el, "id", out var id)) continue;
            var name = TryGetString(el, "name") ?? "(unnamed)";
            var code = TryGetString(el, "code");
            result.Add(new AdsolutAdministrationSummary(id, name, code));
        }
        return result;
    }

    private static bool TryGetGuid(JsonElement el, string name, out Guid value)
    {
        value = Guid.Empty;
        if (!el.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.String)
        {
            return Guid.TryParse(prop.GetString(), out value);
        }
        return false;
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
        if (prop.ValueKind == JsonValueKind.Null) return null;
        return null;
    }
}
