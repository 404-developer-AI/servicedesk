using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        // PUT-as-replace fix (live-test bug, v0.0.27): WK treats every absent
        // optional field as "set to null/default" — pushing only our managed
        // subset wiped country, dueDays, dueDate, bankAccounts, languageIsoCode,
        // financialDiscount*, remindersEnabled on the upstream row. Read-modify-
        // write avoids it: GET the current state, transform from the GET shape
        // (nested {id} objects, `bankAccounts`) into the UpdateCustomerRequest
        // shape (flat *Id strings, `bankAccountLines`), overlay our managed
        // fields, PUT back the full body. Cost: 2× HTTP per update — fine inside
        // the per-tick cap of 200. CreateCustomerAsync stays single-shot because
        // a brand-new row has nothing to preserve.
        var existingJson = await _invoker.SendAsync(
            eventType: AdsolutEventTypes.CustomersGet,
            buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
            parseSuccess: async (response, c) => await response.Content.ReadAsStringAsync(c),
            auditPayload: new
            {
                administrationId,
                customerId,
                source = "pre_update_overlay",
            },
            ct: ct);

        var body = BuildUpdateBody(existingJson, payload, customerId);

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

    /// Build the PUT body in the canonical <c>UpdateCustomerRequest</c> shape.
    /// We start from an explicitly-enumerated template (every slot the schema
    /// defines, in the documented order), fill each slot from the GET response
    /// (with the necessary read-vs-write transformations), then overlay our
    /// SD-managed fields with the no-op-on-equal regel.
    ///
    /// Why "template-then-fill" instead of "GET-then-strip":
    /// <list type="bullet">
    /// <item>Guarantees we never leak a GET-only field into the PUT body
    /// (<c>code</c> / <c>id</c> / <c>lastModified</c> / <c>links</c> /
    /// <c>contacts</c> / <c>postingRuleLines</c> / <c>currency</c>). Even
    /// if WK ever adds a new read-only field, our PUT body stays clean by
    /// construction.</item>
    /// <item>Guarantees every <c>UpdateCustomerRequest</c> slot is present
    /// in the body (with a sensible default if the GET didn't have it),
    /// so WK's import pipeline doesn't branch into a "field-absent" path
    /// that may have its own bugs (the Adsolut AccountingHarbour
    /// <c>POCollection.Populate</c> stale-DataRow exception, observed
    /// 2026-04-30, looks consistent with this hypothesis).</item>
    /// <item>Read-vs-write transformations stay localized: nested <c>{id}</c>
    /// objects from GET (<c>country</c>, <c>vatSpecification</c>,
    /// <c>vatPercentage</c>) become flat <c>countryId</c> / <c>vatSpecificationId</c> /
    /// <c>vatPercentageId</c>; the <c>bankAccounts</c> array becomes
    /// <c>bankAccountLines</c> (item shape identical).</item>
    /// </list>
    /// After the template is filled, the SD-managed overlay only writes a
    /// field when its payload value differs semantically from what WK had
    /// (trim + null≡"" comparison; address compared as recombined full line).
    /// That keeps a no-op push from bumping WK's <c>lastModified</c>.
    ///
    /// <paramref name="overlay"/> may be <c>null</c> — in that case the
    /// returned body is the canonical PUT shape filled purely from the GET
    /// (no SD-managed overrides). Used by the admin debug PUT-preview tool
    /// to show "what we'd send if we PUT this row right now without any
    /// local edits", which is the same body our pusher would produce when
    /// the local copy is already in sync. The <c>customerId</c> parameter
    /// is kept on the signature for symmetry with the URL path id and to
    /// document who the body belongs to.
    public static string BuildUpdateBody(
        string existingJson,
        AdsolutCustomerWritePayload? overlay,
        Guid customerId)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(existingJson);
        }
        catch (JsonException ex)
        {
            throw new AdsolutApiException(
                "Adsolut customer GET (pre-update overlay) returned a non-JSON body: " + ex.Message,
                httpStatus: null,
                upstreamErrorCode: "pre_update_overlay_bad_json");
        }
        if (rootNode is not JsonObject src)
        {
            throw new AdsolutApiException(
                "Adsolut customer GET (pre-update overlay) returned a non-object body.",
                httpStatus: null,
                upstreamErrorCode: "pre_update_overlay_bad_shape");
        }

        // Build the canonical UpdateCustomerRequest template. Every field the
        // schema defines gets a slot here, in the documented order. Values
        // come from GET via the helpers below (which preserve nulls / apply
        // the read-vs-write transformations).
        var dst = new JsonObject
        {
            ["alphaCode"] = ReadStringValueOrNull(src, "alphaCode"),
            ["number"] = ReadStringValueOrNull(src, "number"),
            ["dueDays"] = ReadIntValueOrDefault(src, "dueDays", 0),
            ["dueDate"] = ReadStringValueOrDefault(src, "dueDate", "NotSpecified"),
            ["name"] = ReadStringValueOrDefault(src, "name", string.Empty),
            ["fax"] = ReadStringValueOrNull(src, "fax"),
            ["phone"] = ReadStringValueOrNull(src, "phone"),
            ["mobilePhone"] = ReadStringValueOrNull(src, "mobilePhone"),
            ["email"] = ReadStringValueOrNull(src, "email"),
            ["postalCode"] = ReadStringValueOrNull(src, "postalCode"),
            ["city"] = ReadStringValueOrNull(src, "city"),
            ["streetName"] = ReadStringValueOrNull(src, "streetName"),
            ["streetNumber"] = ReadStringValueOrNull(src, "streetNumber"),
            ["boxNumber"] = ReadStringValueOrNull(src, "boxNumber"),
            ["countryId"] = ReadNestedIdOrNull(src, "country"),
            ["vatNumber"] = ReadStringValueOrNull(src, "vatNumber"),
            ["countryPrefixVatNumber"] = ReadStringValueOrDefault(src, "countryPrefixVatNumber", string.Empty),
            ["vatSpecificationId"] = ReadNestedIdOrNull(src, "vatSpecification"),
            ["vatPercentageId"] = ReadNestedIdOrNull(src, "vatPercentage"),
            // GET (CustomerDetailResponse) does not expose `active`; PUT
            // requires it as non-nullable bool. Default true: every customer
            // we have a GET for is by definition still listed by WK, which
            // for v0.0.27 means active. The eventual deactivate-from-SD
            // story (out of scope for this version) will need an admin
            // setting to flip this default.
            ["active"] = true,
            ["bankAccountLines"] = ReadBankAccountLines(src),
            ["externalId"] = ReadStringValueOrNull(src, "externalId"),
            ["languageIsoCode"] = ReadStringValueOrNull(src, "languageIsoCode"),
            ["financialDiscountPercentage"] = ReadDecimalValueOrNull(src, "financialDiscountPercentage"),
            ["financialDiscountDays"] = ReadIntValueOrNull(src, "financialDiscountDays"),
            ["remindersEnabled"] = ReadBoolValueOrDefault(src, "remindersEnabled", true),
            ["remarks"] = ReadStringValueOrNull(src, "remarks"),
        };

        // Overlay SD-managed fields with no-op-on-equal regel — see
        // OverlayString / OverlayIdentifier / OverlayAddress for details.
        // Skipped entirely when overlay is null (debug preview path).
        if (overlay is not null)
        {
            OverlayString(dst, "name", overlay.Name);
            OverlayIdentifier(dst, "alphaCode", overlay.AlphaCode);
            OverlayIdentifier(dst, "number", overlay.Number);
            OverlayString(dst, "email", overlay.Email);
            OverlayString(dst, "phone", overlay.Phone);
            OverlayString(dst, "postalCode", overlay.PostalCode);
            OverlayString(dst, "city", overlay.City);
            OverlayString(dst, "vatNumber", overlay.VatNumber);
            OverlayString(dst, "countryPrefixVatNumber", overlay.CountryPrefixVatNumber);
            OverlayAddress(dst, overlay.StreetName, overlay.StreetNumber, overlay.BoxNumber);
        }

        return dst.ToJsonString();
    }

    private static JsonNode? ReadStringValueOrNull(JsonObject src, string key)
    {
        var s = ReadStringValue(src, key);
        return s is null ? null : JsonValue.Create(s);
    }

    private static JsonNode? ReadStringValueOrDefault(JsonObject src, string key, string fallback)
    {
        var s = ReadStringValue(src, key);
        return JsonValue.Create(s ?? fallback);
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

    private static JsonNode ReadIntValueOrDefault(JsonObject src, string key, int fallback)
    {
        if (src.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
            jv.TryGetValue<int>(out var i))
        {
            return JsonValue.Create(i);
        }
        return JsonValue.Create(fallback);
    }

    private static JsonNode? ReadIntValueOrNull(JsonObject src, string key)
    {
        if (src.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
            jv.TryGetValue<int>(out var i))
        {
            return JsonValue.Create(i);
        }
        return null;
    }

    private static JsonNode? ReadDecimalValueOrNull(JsonObject src, string key)
    {
        if (src.TryGetPropertyValue(key, out var v) && v is JsonValue jv)
        {
            if (jv.TryGetValue<decimal>(out var d)) return JsonValue.Create(d);
            if (jv.TryGetValue<double>(out var dd)) return JsonValue.Create(dd);
            if (jv.TryGetValue<int>(out var i)) return JsonValue.Create(i);
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

    private static JsonArray ReadBankAccountLines(JsonObject src)
    {
        // GET response uses `bankAccounts`; PUT shape uses `bankAccountLines`.
        // Item schema (default, iban, bicCode) is identical between the two.
        var lines = new JsonArray();
        if (src["bankAccounts"] is JsonArray banks)
        {
            foreach (var b in banks)
            {
                if (b is JsonObject bo)
                {
                    var copy = new JsonObject
                    {
                        ["default"] = ReadBoolValueOrDefault(bo, "default", false),
                        ["iban"] = ReadStringValueOrDefault(bo, "iban", string.Empty),
                        ["bicCode"] = ReadStringValueOrNull(bo, "bicCode"),
                    };
                    lines.Add(copy);
                }
            }
        }
        return lines;
    }

    /// Overwrite <paramref name="key"/> in <paramref name="dst"/> with
    /// <paramref name="payloadValue"/> only when the payload value is
    /// semantically different from what's already there. "Semantically equal"
    /// means trimmed-and-null-treated-as-empty equality — this catches both
    /// `null` vs `""` (which the SD pusher loves to introduce) and accidental
    /// trailing-whitespace differences. When equal, the existing GET value
    /// (which CopyIfPresent put there earlier) stays untouched, so the PUT
    /// reads identical to what WK already has and doesn't bump lastModified.
    private static void OverlayString(JsonObject dst, string key, string? payloadValue)
    {
        var existing = ReadStringValue(dst, key);
        if (SemanticallyEqual(payloadValue, existing)) return;
        dst[key] = payloadValue ?? string.Empty;
    }

    /// Variant for `alphaCode` / `number`: immutable upstream identifiers.
    /// Only overlay when the payload carries a value (so a local row that
    /// pre-dates the column being populated doesn't blank WK's value) AND
    /// when that value semantically differs from what WK already has (so
    /// echoing back the same identifier is a no-op).
    private static void OverlayIdentifier(JsonObject dst, string key, string? payloadValue)
    {
        if (string.IsNullOrEmpty(payloadValue)) return;
        var existing = ReadStringValue(dst, key);
        if (SemanticallyEqual(payloadValue, existing)) return;
        dst[key] = payloadValue;
    }

    /// Address overlay treats <c>streetName</c> + <c>streetNumber</c> +
    /// <c>boxNumber</c> as one logical field. The SD push-side splits a
    /// joined address line via a trailing-digit heuristic that doesn't
    /// match how Adsolut users distribute the parts, so a per-field compare
    /// would always look "changed". Instead we recombine both sides into
    /// a canonical full address string and only overlay all three fields
    /// when those strings differ. When equal we leave WK's distribution
    /// (whatever it is — full street incl. number on streetName, or split,
    /// or whatever a human typed in Adsolut) untouched.
    private static void OverlayAddress(
        JsonObject dst, string? streetName, string? streetNumber, string? boxNumber)
    {
        var existingLine = RecombineAddress(
            ReadStringValue(dst, "streetName"),
            ReadStringValue(dst, "streetNumber"),
            ReadStringValue(dst, "boxNumber"));
        var payloadLine = RecombineAddress(streetName, streetNumber, boxNumber);
        if (string.Equals(existingLine, payloadLine, StringComparison.OrdinalIgnoreCase)) return;

        dst["streetName"] = streetName ?? string.Empty;
        dst["streetNumber"] = streetNumber ?? string.Empty;
        dst["boxNumber"] = boxNumber ?? string.Empty;
    }

    private static string RecombineAddress(string? streetName, string? streetNumber, string? boxNumber)
    {
        var sn = (streetName ?? string.Empty).Trim();
        var num = (streetNumber ?? string.Empty).Trim();
        var box = (boxNumber ?? string.Empty).Trim();
        var line = sn;
        if (num.Length > 0)
        {
            line = line.Length == 0 ? num : line + " " + num;
        }
        if (box.Length > 0)
        {
            line = line.Length == 0 ? "bus " + box : line + " bus " + box;
        }
        return line;
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
