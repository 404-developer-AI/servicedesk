using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Thin wrapper over <see cref="AdsolutHttpInvoker"/> for the two Adsolut
/// Accounting write-paths the v0.0.27 push-tak needs: create a customer and
/// update one. Both calls audit a single row in <c>integration_audit</c>;
/// the read-back GET (when the write response did not include a lastModified)
/// is a separate <see cref="AdsolutEventTypes.CustomersGet"/> row so admins
/// can tell push-traffic from read-back-traffic at a glance.
public sealed class AdsolutCustomersWriteClient : IAdsolutCustomersWriteClient
{
    private readonly AdsolutHttpInvoker _invoker;
    private readonly ILogger<AdsolutCustomersWriteClient> _logger;

    public AdsolutCustomersWriteClient(
        AdsolutHttpInvoker invoker,
        ILogger<AdsolutCustomersWriteClient> logger)
    {
        _invoker = invoker;
        _logger = logger;
    }

    public async Task<AdsolutCustomerWriteResult> CreateCustomerAsync(
        Guid administrationId,
        AdsolutCustomerWritePayload payload,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers";
        var body = SerializePayload(payload, includeId: null);

        var parsed = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomersCreate,
            buildRequest: () => BuildJsonRequest(HttpMethod.Post, url, body),
            parseSuccess: async (response, c) =>
            {
                var raw = await response.Content.ReadAsStringAsync(c);
                return ParseWriteResponse(raw);
            },
            auditPayload: new
            {
                administrationId,
                alphaCode = payload.AlphaCode,
                number = payload.Number,
                hasVat = !string.IsNullOrEmpty(payload.VatNumber),
            },
            ct: ct);

        if (parsed.Id == Guid.Empty)
        {
            // Adsolut accepted the POST but did not echo the new id back —
            // fall through is not possible because we have no id to GET.
            throw new AdsolutApiException(
                "Adsolut customers.create response did not carry an id; cannot link the local row.",
                httpStatus: null,
                upstreamErrorCode: "missing_id_in_response");
        }

        if (parsed.LastModified is null)
        {
            // Some WK Accounting endpoints return only `{ "id": ... }` on a
            // successful POST — refetch the canonical row so the push-tak
            // can persist the timestamp + close the loop.
            return await ReadBackAsync(administrationId, parsed.Id, ct);
        }

        return parsed;
    }

    public async Task<AdsolutCustomerWriteResult> UpdateCustomerAsync(
        Guid administrationId,
        Guid customerId,
        AdsolutCustomerWritePayload payload,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{customerId}";
        var body = SerializePayload(payload, includeId: customerId);

        var parsed = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomersUpdate,
            buildRequest: () => BuildJsonRequest(HttpMethod.Put, url, body),
            parseSuccess: async (response, c) =>
            {
                var raw = await response.Content.ReadAsStringAsync(c);
                return ParseWriteResponse(raw);
            },
            auditPayload: new
            {
                administrationId,
                customerId,
                hasVat = !string.IsNullOrEmpty(payload.VatNumber),
            },
            ct: ct);

        // PUT may return 204 NoContent (empty body, parsed.Id == Empty,
        // parsed.LastModified == null). Fall back to a GET so the push-tak
        // gets the freshly stamped lastModified to persist alongside the
        // hash. The id we already know — it was the input.
        if (parsed.LastModified is null)
        {
            return await ReadBackAsync(administrationId, customerId, ct);
        }

        // Defensive: if WK echoed back a different id than the URL path
        // (should never happen), trust the path id.
        return parsed.Id == Guid.Empty
            ? new AdsolutCustomerWriteResult(customerId, parsed.LastModified, parsed.AlphaCode, parsed.Number)
            : parsed;
    }

    private async Task<AdsolutCustomerWriteResult> ReadBackAsync(
        Guid administrationId,
        Guid customerId,
        CancellationToken ct)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{customerId}";

        var parsed = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomersGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var raw = await response.Content.ReadAsStringAsync(c);
                return ParseWriteResponse(raw);
            },
            auditPayload: new { administrationId, customerId, source = "post_or_put_readback" },
            ct: ct);

        if (parsed.Id == Guid.Empty)
        {
            // The GET succeeded but the body shape is unexpected. Return
            // the path id so the push-tak can still close the loop on its
            // side; the lastModified being null means the next pull will
            // re-evaluate.
            return new AdsolutCustomerWriteResult(customerId, parsed.LastModified, parsed.AlphaCode, parsed.Number);
        }
        return parsed;
    }

    private static HttpRequestMessage BuildJsonRequest(HttpMethod method, string url, string body) =>
        new(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string SerializePayload(AdsolutCustomerWritePayload p, Guid? includeId)
    {
        // Hand-rolled serialization keeps the wire shape obvious and means
        // we never accidentally serialize a stray field a future struct
        // change adds. The shape mirrors WK's UpdateCustomerRequest /
        // AddCustomerRequest schemas: `alphaCode` + `number` instead of
        // the read-only `code` field; no `country` (write-shape uses
        // `countryId` UUID which v0.0.27 doesn't resolve yet).
        //
        // `number` (UI label "klantnummer" in Adsolut) is immutable post-
        // creation. Send it back unchanged from the last pull; an absent
        // value triggers `UpdateCustomerNumberNotValid`. The pusher gate
        // (`SkippedMissingAdsolutNumber`) skips rows that were pulled
        // before the column existed.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            if (includeId is { } id) writer.WriteString("id", id);
            writer.WriteString("name", p.Name ?? string.Empty);
            if (!string.IsNullOrEmpty(p.AlphaCode)) writer.WriteString("alphaCode", p.AlphaCode);
            if (!string.IsNullOrEmpty(p.Number)) writer.WriteString("number", p.Number);
            writer.WriteString("email", p.Email ?? string.Empty);
            writer.WriteString("phone", p.Phone ?? string.Empty);
            writer.WriteString("streetName", p.StreetName ?? string.Empty);
            writer.WriteString("streetNumber", p.StreetNumber ?? string.Empty);
            writer.WriteString("boxNumber", p.BoxNumber ?? string.Empty);
            writer.WriteString("postalCode", p.PostalCode ?? string.Empty);
            writer.WriteString("city", p.City ?? string.Empty);
            writer.WriteString("vatNumber", p.VatNumber ?? string.Empty);
            writer.WriteString("countryPrefixVatNumber", p.CountryPrefixVatNumber ?? string.Empty);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// Parse the body of a POST/PUT/GET on /customers. Tolerates both the
    /// full row shape and the slim `{ id, lastModified }` echo some WK
    /// endpoints return on writes. Returns <c>(Empty, null, null, null)</c>
    /// when the body is empty (204 NoContent / 200 with no body) so the
    /// caller can route to the read-back fallback.
    internal static AdsolutCustomerWriteResult ParseWriteResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutCustomerWriteResult(Guid.Empty, null, null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AdsolutCustomerWriteResult(Guid.Empty, null, null, null);
            }

            Guid id = Guid.Empty;
            if (root.TryGetProperty("id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String &&
                Guid.TryParse(idEl.GetString(), out var parsedId))
            {
                id = parsedId;
            }

            DateTimeOffset? lastModified = null;
            if (root.TryGetProperty("lastModified", out var lmEl) &&
                lmEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(lmEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var lm))
            {
                lastModified = lm.ToUniversalTime();
            }

            string? alphaCode = null;
            if (root.TryGetProperty("alphaCode", out var acEl) &&
                acEl.ValueKind == JsonValueKind.String)
            {
                alphaCode = acEl.GetString();
            }

            string? number = null;
            if (root.TryGetProperty("number", out var nEl) &&
                nEl.ValueKind == JsonValueKind.String)
            {
                number = nEl.GetString();
            }

            return new AdsolutCustomerWriteResult(id, lastModified, alphaCode, number);
        }
        catch (JsonException)
        {
            // Non-JSON body — treat as empty echo and let the caller route
            // to the GET fallback. Letting this throw would force every
            // caller to wrap with the same recovery logic.
            return new AdsolutCustomerWriteResult(Guid.Empty, null, null, null);
        }
    }
}
