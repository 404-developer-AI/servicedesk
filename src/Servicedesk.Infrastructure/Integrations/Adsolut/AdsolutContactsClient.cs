using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Integrations.Http;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// v0.0.28 — pulls customer-contacts (and supplier-contacts) from the
/// active Adsolut administration. Mirrors the parse-and-canary discipline
/// of <see cref="AdsolutCustomersClient"/>: defensive against shape
/// changes (bare array vs. paged-result wrapper) and warns once per call
/// when an offset-less <c>lastModified</c> shows up so we catch a WK
/// serialisation drift before it silently absorbs a 1-2h skew.
public sealed class AdsolutContactsClient : IAdsolutContactsClient
{
    private readonly AdsolutHttpInvoker _invoker;
    private readonly ILogger<AdsolutContactsClient> _logger;

    public AdsolutContactsClient(
        AdsolutHttpInvoker invoker,
        ILogger<AdsolutContactsClient> logger)
    {
        _invoker = invoker;
        _logger = logger;
    }

    public Task<IReadOnlyList<AdsolutContact>> ListCustomerContactsAsync(
        Guid administrationId,
        Guid customerId,
        CancellationToken ct = default)
        => ListAsync(administrationId, "customers", customerId, AdsolutEventTypes.CustomerContactsList, ct);

    public Task<IReadOnlyList<AdsolutContact>> ListSupplierContactsAsync(
        Guid administrationId,
        Guid supplierId,
        CancellationToken ct = default)
        => ListAsync(administrationId, "suppliers", supplierId, AdsolutEventTypes.SupplierContactsList, ct);

    private async Task<IReadOnlyList<AdsolutContact>> ListAsync(
        Guid administrationId,
        string parentResource,
        Guid parentId,
        string eventType,
        CancellationToken ct)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/{parentResource}/{parentId}/contacts";

        var parsed = await _invoker.SendAsync(
            eventType: eventType,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var body = await response.Content.ReadAsStringAsync(c);
                return ParseContacts(body);
            },
            auditPayload: new
            {
                administrationId,
                parent = parentResource,
                parentId,
            },
            ct: ct);

        if (parsed.OffsetlessLastModifiedCount > 0)
        {
            _logger.LogWarning(
                "Adsolut {Resource}/{ParentId}/contacts returned {Count} offset-less lastModified values; parser falls back to AssumeUniversal. Review WK serialisation immediately.",
                parentResource,
                parentId,
                parsed.OffsetlessLastModifiedCount);
        }

        return parsed.Contacts;
    }

    internal readonly record struct ParseResult(
        IReadOnlyList<AdsolutContact> Contacts,
        int OffsetlessLastModifiedCount);

    /// Visible to tests — pins the bare-array vs paged-result tolerance and
    /// the offset-less <c>lastModified</c> canary count.
    internal static ParseResult ParseContacts(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ParseResult(Array.Empty<AdsolutContact>(), 0);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Per OpenAPI the contacts endpoint returns a bare array. Defensive
        // parser still accepts the documented `{ items: [...] }` paged-result
        // shape in case WK switches conventions on us — same approach as
        // AdsolutCustomersClient.
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            items = itemsEl;
        }
        else
        {
            return new ParseResult(Array.Empty<AdsolutContact>(), 0);
        }

        var list = new List<AdsolutContact>(items.GetArrayLength());
        var offsetlessCount = 0;
        foreach (var el in items.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetGuidProperty("id", out var id)) continue;

            var (lastModified, wasOffsetless) = TryGetDateTimeOffset(el, "lastModified");
            if (wasOffsetless) offsetlessCount++;

            // Trim email at the parse boundary so the upserter never has
            // to worry about " wendy@x.be " vs "wendy@x.be" — CITEXT is
            // case-insensitive but not whitespace-insensitive, so a stray
            // space would otherwise miss an existing-contact match and
            // trigger the unique-constraint on insert.
            list.Add(new AdsolutContact(
                Id: id,
                FirstName: el.GetStringOrNull("firstName") ?? string.Empty,
                LastName: el.GetStringOrNull("lastName") ?? string.Empty,
                Email: (el.GetStringOrNull("email") ?? string.Empty).Trim(),
                Phone: el.GetStringOrNull("phone") ?? string.Empty,
                MobilePhone: el.GetStringOrNull("mobilePhone") ?? string.Empty,
                Active: el.GetBool("active", defaultValue: true),
                LastModified: lastModified));
        }

        return new ParseResult(list, offsetlessCount);
    }

    private static (DateTimeOffset? Value, bool WasOffsetless) TryGetDateTimeOffset(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return (null, false);
        }
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return (null, false);

        var offsetless = AdsolutCustomersClient.LooksOffsetless(raw);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? (dto.ToUniversalTime(), offsetless)
            : (null, offsetless);
    }
}
