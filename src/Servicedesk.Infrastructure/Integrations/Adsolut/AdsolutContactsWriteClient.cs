using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// v0.0.29 — write-client for Adsolut customer-contacts. Mirrors the v0.0.27
/// <see cref="AdsolutCustomersWriteClient"/> discipline:
/// <list type="bullet">
/// <item>POST /acc/v1/adm/{adm}/customers/{customer}/contacts to add a
/// fresh contact, PUT /…/contacts/{contact} to update an existing one.</item>
/// <item>POST returns <c>AddEntityWithValidationResponse</c> (id only, no
/// lastModified); PUT returns <c>ValidationResponse</c> (validationResults
/// only). Both paths fall back to a read-back GET so the push-tak gets the
/// upstream <c>lastModified</c> stamp it needs to keep the echo-pull loop
/// quiet.</item>
/// <item>PUT is read-modify-write: the WK API does not support PATCH and
/// treats every absent optional field on PUT as "set to null/default" (same
/// trap as customers). We GET the current row, build the canonical
/// <c>UpdateCustomerContactRequest</c> from it, overlay our four managed
/// fields with no-op-on-equal regel, and PUT the full body back. CreatePath
/// stays single-shot — a brand-new contact has nothing to preserve.</item>
/// </list>
///
/// Managed fields (locked-in for v0.0.29, identical to the v0.0.28 pull
/// mirror): <c>firstName</c>, <c>lastName</c>, <c>phone</c>, <c>mobilePhone</c>.
/// Every other field on the schema (<c>fax</c>, <c>memo</c>, <c>address</c>,
/// <c>city</c>, <c>postalCode</c>, <c>countryId</c>, the four <c>for*</c>
/// flags, <c>dateOfBirth</c>, <c>nationalIdentificationNumber</c>,
/// <c>languageIsoCode</c>, <c>externalId</c>, <c>active</c>) is preserved
/// verbatim from the GET — SD never edits these and never overrides them
/// on push.
///
/// Special case for <c>name</c>: it is required on both add + update
/// requests (<c>minLength: 1</c>) but nullable on the GET response. When
/// WK returns a null/empty name we fall back to <c>firstName + " " + lastName</c>
/// from the overlaid first/last so the PUT does not 400. This is the only
/// SD-derived value we compute; we never overwrite a non-empty WK name.
public sealed class AdsolutContactsWriteClient : IAdsolutContactsWriteClient
{
    private readonly AdsolutHttpInvoker _invoker;
    private readonly ILogger<AdsolutContactsWriteClient> _logger;

    public AdsolutContactsWriteClient(
        AdsolutHttpInvoker invoker,
        ILogger<AdsolutContactsWriteClient> logger)
    {
        _invoker = invoker;
        _logger = logger;
    }

    public async Task<AdsolutContactWriteResult> CreateCustomerContactAsync(
        Guid administrationId,
        Guid customerId,
        AdsolutContactWritePayload payload,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var listUrl = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{customerId}/contacts";
        var body = SerializeAddPayload(payload);

        var parsed = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomerContactsCreate,
            buildRequest: () => BuildJsonRequest(HttpMethod.Post, listUrl, body),
            parseSuccess: async (response, c) =>
            {
                var raw = await response.Content.ReadAsStringAsync(c);
                return ParseWriteResponse(raw);
            },
            auditPayload: new
            {
                administrationId,
                customerId,
                hasEmail = !string.IsNullOrEmpty(payload.Email),
            },
            ct: ct);

        if (parsed.Id == Guid.Empty)
        {
            throw new AdsolutApiException(
                "Adsolut customer_contacts.create response did not carry an id; cannot link the local row.",
                httpStatus: null,
                upstreamErrorCode: "missing_id_in_response");
        }

        if (parsed.LastModified is null)
        {
            return await ReadBackAsync(administrationId, customerId, parsed.Id, source: "post_readback", ct);
        }

        return parsed;
    }

    public async Task<AdsolutContactWriteResult> UpdateCustomerContactAsync(
        Guid administrationId,
        Guid customerId,
        Guid contactId,
        AdsolutContactWritePayload payload,
        CancellationToken ct = default)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{customerId}/contacts/{contactId}";

        // Same RMW reasoning as customers (see v0.0.27 lessons): PUT is a
        // total replace, every absent optional field reverts to default/null
        // upstream. GET → build canonical body from the response → overlay
        // our four managed fields → PUT.
        var existingJson = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomerContactsGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) => await response.Content.ReadAsStringAsync(c),
            auditPayload: new
            {
                administrationId,
                customerId,
                contactId,
                source = "pre_update_overlay",
            },
            ct: ct);

        var body = BuildUpdateBody(existingJson, payload, contactId);

        var parsed = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomerContactsUpdate,
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
                contactId,
            },
            ct: ct);

        // PUT response shape (ValidationResponse) carries no id and no
        // lastModified. Read back so the pusher can anchor the link
        // timestamp on the upstream stamp.
        if (parsed.LastModified is null)
        {
            return await ReadBackAsync(administrationId, customerId, contactId, source: "put_readback", ct);
        }

        return parsed.Id == Guid.Empty
            ? new AdsolutContactWriteResult(contactId, parsed.LastModified)
            : parsed;
    }

    private async Task<AdsolutContactWriteResult> ReadBackAsync(
        Guid administrationId,
        Guid customerId,
        Guid contactId,
        string source,
        CancellationToken ct)
    {
        var baseUrl = await _invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{customerId}/contacts/{contactId}";

        var parsed = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomerContactsGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) =>
            {
                var raw = await response.Content.ReadAsStringAsync(c);
                return ParseWriteResponse(raw);
            },
            auditPayload: new { administrationId, customerId, contactId, source },
            ct: ct);

        return parsed.Id == Guid.Empty
            ? new AdsolutContactWriteResult(contactId, parsed.LastModified)
            : parsed;
    }

    private static HttpRequestMessage BuildJsonRequest(HttpMethod method, string url, string body) =>
        new(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// CREATE path body. Brand-new row, nothing to preserve — the four
    /// managed fields plus the synthesised <c>name</c> + the email + every
    /// canonical slot the schema requires (with sensible defaults). Every
    /// other field is emitted as JSON null so WK doesn't reach into a
    /// "field-absent" pipeline.
    private static string SerializeAddPayload(AdsolutContactWritePayload p)
    {
        var name = ComposeName(p.FirstName, p.LastName, fallback: "Contact");
        var dst = new JsonObject
        {
            ["name"] = name,
            ["active"] = true,
            ["languageIsoCode"] = null,
            ["firstName"] = p.FirstName ?? string.Empty,
            ["lastName"] = p.LastName ?? string.Empty,
            ["address"] = null,
            ["city"] = null,
            ["postalCode"] = null,
            ["countryId"] = null,
            ["phone"] = p.Phone ?? string.Empty,
            ["mobilePhone"] = p.MobilePhone ?? string.Empty,
            ["email"] = p.Email ?? string.Empty,
            ["fax"] = null,
            ["memo"] = null,
            ["dateOfBirth"] = null,
            ["nationalIdentificationNumber"] = null,
            ["forCommunication"] = false,
            ["forOrderConfirmations"] = false,
            ["forPaymentReminders"] = false,
            ["forInvoiceMails"] = false,
            ["externalId"] = null,
        };
        return dst.ToJsonString();
    }

    /// Build the PUT body in the canonical <c>UpdateCustomerContactRequest</c>
    /// shape. Same template-then-overlay strategy as
    /// <see cref="AdsolutCustomersWriteClient.BuildUpdateBody"/>:
    /// <list type="bullet">
    /// <item>Enumerate every <c>UpdateCustomerContactRequest</c> slot in the
    /// documented order and fill it from the GET response (with the
    /// read-vs-write transformation: nested <c>{ country: { id } }</c>
    /// becomes flat <c>countryId</c>).</item>
    /// <item>Apply non-nullable defaults for fields not on the GET shape
    /// (<c>active</c> defaults to true; the four <c>for*</c> bools default
    /// to false when absent).</item>
    /// <item>Overlay the four SD-managed fields with the no-op-on-equal
    /// rule so a no-edit round-trip produces a body identical to what WK
    /// already has, leaving <c>lastModified</c> unchanged.</item>
    /// <item>Synthesise <c>name</c> from <c>firstName</c> + <c>lastName</c>
    /// only when WK returned no name — never overwrite a non-empty WK
    /// name.</item>
    /// </list>
    /// <paramref name="overlay"/> may be <c>null</c> for diagnostics — the
    /// returned body then is the canonical no-edit PUT shape filled purely
    /// from the GET. <paramref name="contactId"/> is kept on the signature
    /// for symmetry with the URL path id; not embedded in the body (the
    /// schema disallows it via <c>additionalProperties: false</c>).
    public static string BuildUpdateBody(
        string existingJson,
        AdsolutContactWritePayload? overlay,
        Guid contactId)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(existingJson);
        }
        catch (JsonException ex)
        {
            throw new AdsolutApiException(
                "Adsolut customer-contact GET (pre-update overlay) returned a non-JSON body: " + ex.Message,
                httpStatus: null,
                upstreamErrorCode: "pre_update_overlay_bad_json");
        }
        if (rootNode is not JsonObject src)
        {
            throw new AdsolutApiException(
                "Adsolut customer-contact GET (pre-update overlay) returned a non-object body.",
                httpStatus: null,
                upstreamErrorCode: "pre_update_overlay_bad_shape");
        }

        // Canonical UpdateCustomerContactRequest template — every slot the
        // OpenAPI schema defines, in the documented order.
        var dst = new JsonObject
        {
            ["name"] = ReadStringValueOrNull(src, "name"),
            ["active"] = ReadBoolValueOrDefault(src, "active", true),
            ["languageIsoCode"] = ReadStringValueOrNull(src, "languageIsoCode"),
            ["firstName"] = ReadStringValueOrNull(src, "firstName"),
            ["lastName"] = ReadStringValueOrNull(src, "lastName"),
            ["address"] = ReadStringValueOrNull(src, "address"),
            ["city"] = ReadStringValueOrNull(src, "city"),
            ["postalCode"] = ReadStringValueOrNull(src, "postalCode"),
            ["countryId"] = ReadNestedIdOrNull(src, "country"),
            ["phone"] = ReadStringValueOrNull(src, "phone"),
            ["mobilePhone"] = ReadStringValueOrNull(src, "mobilePhone"),
            ["email"] = ReadStringValueOrNull(src, "email"),
            ["fax"] = ReadStringValueOrNull(src, "fax"),
            ["memo"] = ReadStringValueOrNull(src, "memo"),
            ["dateOfBirth"] = ReadStringValueOrNull(src, "dateOfBirth"),
            ["nationalIdentificationNumber"] = ReadStringValueOrNull(src, "nationalIdentificationNumber"),
            ["forCommunication"] = ReadBoolValueOrDefault(src, "forCommunication", false),
            ["forOrderConfirmations"] = ReadBoolValueOrDefault(src, "forOrderConfirmations", false),
            ["forPaymentReminders"] = ReadBoolValueOrDefault(src, "forPaymentReminders", false),
            ["forInvoiceMails"] = ReadBoolValueOrDefault(src, "forInvoiceMails", false),
            ["externalId"] = ReadStringValueOrNull(src, "externalId"),
        };

        if (overlay is not null)
        {
            OverlayString(dst, "firstName", overlay.FirstName);
            OverlayString(dst, "lastName", overlay.LastName);
            OverlayString(dst, "phone", overlay.Phone);
            OverlayString(dst, "mobilePhone", overlay.MobilePhone);
        }

        // `name` is required (minLength: 1). When WK had it null/empty,
        // synthesise it from the (possibly just-overlaid) firstName +
        // lastName so the PUT does not 400. We never overwrite a non-empty
        // WK name — that is Adsolut's display name and may carry
        // formatting an admin chose by hand.
        var existingName = ReadStringValue(dst, "name");
        if (string.IsNullOrWhiteSpace(existingName))
        {
            var overlaidFirst = ReadStringValue(dst, "firstName") ?? string.Empty;
            var overlaidLast = ReadStringValue(dst, "lastName") ?? string.Empty;
            dst["name"] = ComposeName(overlaidFirst, overlaidLast, fallback: "Contact");
        }

        return dst.ToJsonString();
    }

    /// Build a non-empty display-name from the first + last. Falls back to
    /// the literal "Contact" so the PUT never crashes on the
    /// <c>minLength: 1</c> validation when both pieces are blank — that
    /// path is only reachable if WK already had a contact with no name and
    /// SD has no first/last either, which is operationally impossible
    /// (the pull-side requires email) but we still want to be safe.
    private static string ComposeName(string? first, string? last, string fallback)
    {
        var f = (first ?? string.Empty).Trim();
        var l = (last ?? string.Empty).Trim();
        if (f.Length == 0 && l.Length == 0) return fallback;
        if (f.Length == 0) return l;
        if (l.Length == 0) return f;
        return f + " " + l;
    }

    private static JsonNode? ReadStringValueOrNull(JsonObject src, string key)
    {
        var s = ReadStringValue(src, key);
        return s is null ? null : JsonValue.Create(s);
    }

    private static JsonNode? ReadNestedIdOrNull(JsonObject src, string key)
    {
        if (src.TryGetPropertyValue(key, out var value) &&
            value is JsonObject inner &&
            inner.TryGetPropertyValue("id", out var idValue) &&
            idValue is JsonValue iv &&
            iv.TryGetValue<string>(out var s) &&
            !string.IsNullOrEmpty(s))
        {
            return JsonValue.Create(s);
        }
        return null;
    }

    private static JsonNode ReadBoolValueOrDefault(JsonObject src, string key, bool fallback)
    {
        if (src.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
            jv.TryGetValue<bool>(out var b))
        {
            return JsonValue.Create(b);
        }
        return JsonValue.Create(fallback);
    }

    /// Overwrite <paramref name="key"/> in <paramref name="dst"/> with
    /// <paramref name="payloadValue"/> only when the payload value is
    /// semantically different from what's already there. Trim + null≡""
    /// equality — same regel as the v0.0.27 customer overlay.
    private static void OverlayString(JsonObject dst, string key, string? payloadValue)
    {
        var existing = ReadStringValue(dst, key);
        if (SemanticallyEqual(payloadValue, existing)) return;
        dst[key] = payloadValue ?? string.Empty;
    }

    private static string? ReadStringValue(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var v) || v is null) return null;
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
    }

    private static bool SemanticallyEqual(string? a, string? b)
    {
        var pa = (a ?? string.Empty).Trim();
        var pb = (b ?? string.Empty).Trim();
        return pa == pb;
    }

    /// Parse the body of POST/PUT/GET on a contact endpoint. Same
    /// tolerance contract as the customers write-client: empty body,
    /// id-only echo, full row, lowercase Z offset, non-object body, all
    /// produce something the caller can route on without a separate
    /// recovery path.
    internal static AdsolutContactWriteResult ParseWriteResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AdsolutContactWriteResult(Guid.Empty, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AdsolutContactWriteResult(Guid.Empty, null);
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

            return new AdsolutContactWriteResult(id, lastModified);
        }
        catch (JsonException)
        {
            return new AdsolutContactWriteResult(Guid.Empty, null);
        }
    }
}
