using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.27 — pins the response-parser of the customers write-client.
/// The HTTP path is reviewer-trusted; this guards the body-shape tolerance
/// (full row vs. slim id-only echo vs. 204 NoContent) so the push-tak can
/// always link the new row + close the loop without an extra round-trip
/// when WK already returned the canonical state.
public sealed class AdsolutCustomersWriteClientParseTests
{
    [Fact]
    public void Empty_body_yields_empty_id_and_null_lastModified()
    {
        var r = AdsolutCustomersWriteClient.ParseWriteResponse(string.Empty);

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Whitespace_only_body_yields_empty_id_and_null_lastModified()
    {
        var r = AdsolutCustomersWriteClient.ParseWriteResponse("   \r\n  ");

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Slim_id_only_echo_parses_id_and_leaves_lastModified_null()
    {
        var body = """{"id":"22222222-2222-2222-2222-222222222222"}""";
        var r = AdsolutCustomersWriteClient.ParseWriteResponse(body);

        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Full_row_echo_parses_both_id_and_lastModified()
    {
        var body = """
        {
          "id": "33333333-3333-3333-3333-333333333333",
          "name": "Acme",
          "lastModified": "2026-04-29T11:44:20+00:00"
        }
        """;
        var r = AdsolutCustomersWriteClient.ParseWriteResponse(body);

        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), r.Id);
        Assert.NotNull(r.LastModified);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 29, 11, 44, 20, TimeSpan.Zero),
            r.LastModified!.Value);
    }

    [Fact]
    public void Full_row_echo_parses_alphaCode_and_number_when_present()
    {
        // POST /customers returns the WK-assigned klantnummer + alphaCode.
        // Push-tak persists both so the next push doesn't gate on
        // SkippedMissingAdsolutNumber.
        var body = """
        {
          "id": "55555555-5555-5555-5555-555555555555",
          "name": "Acme",
          "code": "CUST-998",
          "alphaCode": "ACM998",
          "number": "998",
          "lastModified": "2026-04-29T11:44:20+00:00"
        }
        """;
        var r = AdsolutCustomersWriteClient.ParseWriteResponse(body);

        Assert.Equal("ACM998", r.AlphaCode);
        Assert.Equal("998", r.Number);
    }

    [Fact]
    public void Slim_id_only_echo_leaves_alphaCode_and_number_null()
    {
        var r = AdsolutCustomersWriteClient.ParseWriteResponse("""{"id":"66666666-6666-6666-6666-666666666666"}""");

        Assert.Null(r.AlphaCode);
        Assert.Null(r.Number);
    }

    [Fact]
    public void Lowercase_z_offset_parses()
    {
        var body = """{"id":"44444444-4444-4444-4444-444444444444","lastModified":"2026-04-29T11:44:20Z"}""";
        var r = AdsolutCustomersWriteClient.ParseWriteResponse(body);

        Assert.NotNull(r.LastModified);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 29, 11, 44, 20, TimeSpan.Zero),
            r.LastModified!.Value);
    }

    [Fact]
    public void Non_object_body_yields_empty_result()
    {
        // WK should never return a JSON array or scalar on a single-row
        // POST/PUT, but a buggy proxy might. Don't throw — push-tak then
        // routes to the GET fallback which retries cleanly.
        var r = AdsolutCustomersWriteClient.ParseWriteResponse("[]");

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Non_json_body_yields_empty_result()
    {
        // Some 5xx pages come back as HTML; we never expect them on a 2xx
        // path but the parser still needs to refuse-to-throw.
        var r = AdsolutCustomersWriteClient.ParseWriteResponse("<html>oops</html>");

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    // ---- BuildUpdateBody overlay tests --------------------------------
    //
    // v0.0.27 — pin the read-modify-write transformation that prevents the
    // PUT-as-replace data-loss bug surfaced by the live test against the
    // real dossier (manual-logs/customer_org.txt vs customer_new.txt).
    //
    // Every test starts from a CustomerDetailResponse-shaped fixture that
    // mirrors what WK actually returns (nested {id} objects, `bankAccounts`
    // array, `code` + `lastModified` + `links` extras), and asserts what
    // ends up in the PUT body — both what's preserved (every unmanaged
    // field), what's transformed (countryId / vatSpecificationId /
    // vatPercentageId / bankAccountLines), and what's dropped (code /
    // lastModified / links / contacts / postingRuleLines / currency).

    /// Mirrors the live customer_org.txt response so the tests fail if WK
    /// ever changes the GET shape (the overlay is defensive against that
    /// but the field-by-field assertions below would diverge).
    private const string FullGetShape = """
    {
      "name": "Datawolk BV",
      "alphaCode": null,
      "code": "000",
      "number": "000",
      "streetName": "Kaulillerweg",
      "streetNumber": "4",
      "boxNumber": null,
      "postalCode": "3950",
      "city": "Bocholt",
      "country": { "id": "5c99e5ad-2091-4717-bdd1-c529a8e8934b" },
      "vatNumber": "0740997252",
      "countryPrefixVatNumber": "BE",
      "phone": "+3289399392",
      "mobilePhone": null,
      "fax": null,
      "email": null,
      "dueDays": 15,
      "dueDate": "AddDueDays",
      "vatSpecification": { "id": "f421c0f4-6c0d-47b2-b2d8-18c87b033973" },
      "vatPercentage": { "id": "bcfd77de-df82-4f5a-b595-3b9fe39e856e", "percentage": 21 },
      "bankAccounts": [
        { "default": true, "iban": "BE13103064987139", "bicCode": "NICABEBB" }
      ],
      "postingRuleLines": [
        { "vatSpecificationId": "f421c0f4-6c0d-47b2-b2d8-18c87b033973", "vatPercentageId": "bcfd77de-df82-4f5a-b595-3b9fe39e856e", "generalLedgerId": "983cabbf-03d8-419e-a4b0-44ca6e7e405d" }
      ],
      "currency": { "id": "e43d27e7-571e-4e7a-a26a-34cd6bffe76c", "nameNl": "Euro" },
      "languageIsoCode": "nl",
      "financialDiscountPercentage": 0,
      "financialDiscountDays": 0,
      "remindersEnabled": true,
      "remarks": null,
      "id": "7d3b86be-8075-4b40-b3e2-cd76e6ac529e",
      "lastModified": "2026-04-29T10:12:15+00:00",
      "links": []
    }
    """;

    private static readonly Guid CustomerId = Guid.Parse("7d3b86be-8075-4b40-b3e2-cd76e6ac529e");

    private static AdsolutCustomerWritePayload PayloadOnlyNameChanged() =>
        // Mirrors what the SD pusher would build for "rename to (Test)" —
        // every other managed field stays at the value the pull last persisted.
        new(
            Name: "Datawolk BV (Test)",
            AlphaCode: null,
            Number: "000",
            Email: "",
            Phone: "+3289399392",
            StreetName: "Kaulillerweg",
            StreetNumber: "4",
            BoxNumber: "",
            PostalCode: "3950",
            City: "Bocholt",
            VatNumber: "0740997252",
            CountryPrefixVatNumber: "BE");

    private static JsonElement Build(string get, AdsolutCustomerWritePayload payload, Guid id)
    {
        var raw = AdsolutCustomersWriteClient.BuildUpdateBody(get, payload, id);
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    [Fact]
    public void Overlay_lifts_country_id_to_flat_countryId()
    {
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        Assert.False(body.TryGetProperty("country", out _),
            "PUT shape uses countryId, not nested country object");
        Assert.Equal(JsonValueKind.String, body.GetProperty("countryId").ValueKind);
        Assert.Equal("5c99e5ad-2091-4717-bdd1-c529a8e8934b", body.GetProperty("countryId").GetString());
    }

    [Fact]
    public void Overlay_lifts_vatSpecification_and_vatPercentage_ids()
    {
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        Assert.False(body.TryGetProperty("vatSpecification", out _));
        Assert.False(body.TryGetProperty("vatPercentage", out _));
        Assert.Equal("f421c0f4-6c0d-47b2-b2d8-18c87b033973", body.GetProperty("vatSpecificationId").GetString());
        Assert.Equal("bcfd77de-df82-4f5a-b595-3b9fe39e856e", body.GetProperty("vatPercentageId").GetString());
    }

    [Fact]
    public void Overlay_renames_bankAccounts_to_bankAccountLines_with_item_shape_intact()
    {
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        Assert.False(body.TryGetProperty("bankAccounts", out _));
        var lines = body.GetProperty("bankAccountLines");
        Assert.Equal(JsonValueKind.Array, lines.ValueKind);
        Assert.Equal(1, lines.GetArrayLength());
        var first = lines[0];
        Assert.True(first.GetProperty("default").GetBoolean());
        Assert.Equal("BE13103064987139", first.GetProperty("iban").GetString());
        Assert.Equal("NICABEBB", first.GetProperty("bicCode").GetString());
    }

    [Fact]
    public void Overlay_preserves_unmanaged_scalars_against_default_reset()
    {
        // Exactly the fields that disappeared in the live test
        // (customer_new.txt) — they must round-trip through the overlay
        // unchanged so WK doesn't reset them to its defaults.
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        Assert.Equal(15, body.GetProperty("dueDays").GetInt32());
        Assert.Equal("AddDueDays", body.GetProperty("dueDate").GetString());
        Assert.Equal("nl", body.GetProperty("languageIsoCode").GetString());
        Assert.Equal(0, body.GetProperty("financialDiscountPercentage").GetInt32());
        Assert.Equal(0, body.GetProperty("financialDiscountDays").GetInt32());
        Assert.True(body.GetProperty("remindersEnabled").GetBoolean());
    }

    [Fact]
    public void Overlay_drops_read_only_and_separate_endpoint_fields()
    {
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        // Read-only / server-controlled / wrong-endpoint — UpdateCustomerRequest
        // has no slot for these.
        Assert.False(body.TryGetProperty("code", out _));
        Assert.False(body.TryGetProperty("lastModified", out _));
        Assert.False(body.TryGetProperty("links", out _));
        Assert.False(body.TryGetProperty("contacts", out _));
        Assert.False(body.TryGetProperty("postingRuleLines", out _));
        Assert.False(body.TryGetProperty("currency", out _));
    }

    [Fact]
    public void Overlay_overwrites_managed_fields_from_payload()
    {
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        // Name is the field the user actually changed.
        Assert.Equal("Datawolk BV (Test)", body.GetProperty("name").GetString());
        // Other managed fields land verbatim from the payload (same as GET
        // here, but the overlay path is what writes them).
        Assert.Equal("Kaulillerweg", body.GetProperty("streetName").GetString());
        Assert.Equal("4", body.GetProperty("streetNumber").GetString());
        Assert.Equal("3950", body.GetProperty("postalCode").GetString());
        Assert.Equal("Bocholt", body.GetProperty("city").GetString());
        Assert.Equal("BE", body.GetProperty("countryPrefixVatNumber").GetString());
        Assert.Equal("0740997252", body.GetProperty("vatNumber").GetString());
        Assert.Equal("+3289399392", body.GetProperty("phone").GetString());
    }

    [Fact]
    public void Overlay_leaves_null_field_untouched_when_payload_is_also_empty()
    {
        // Live-test bug: SD normalizes empty fields to "" while WK keeps
        // them as null; writing "" over null is a meaningless mutation that
        // bumps WK's lastModified and makes the next pull look like drift.
        // FullGetShape has email=null + payload has email="" — overlay must
        // skip and leave the body's email as JSON null (carried through by
        // CopyIfPresent), not overwrite it with "".
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        Assert.True(body.TryGetProperty("email", out var email));
        Assert.Equal(JsonValueKind.Null, email.ValueKind);
    }

    [Fact]
    public void Overlay_writes_empty_managed_string_when_get_has_a_real_value_to_clear()
    {
        // The clear-field intent still works when WK actually has a value:
        // payload email="" + GET email="user@example.com" must overwrite
        // with "" so the upstream row gets cleared.
        var get = """
        { "name": "Acme", "email": "user@example.com",
          "country": { "id": "11111111-1111-1111-1111-111111111111" } }
        """;

        var body = Build(get, PayloadOnlyNameChanged(), CustomerId);

        Assert.True(body.TryGetProperty("email", out var email));
        Assert.Equal(JsonValueKind.String, email.ValueKind);
        Assert.Equal(string.Empty, email.GetString());
    }

    [Fact]
    public void Overlay_leaves_split_address_alone_when_recombination_matches_get()
    {
        // Real reproducer: WK stored "Oude Asserweg 7" as
        // streetName="Oude Asserweg 7" + streetNumber=null + boxNumber=null.
        // The SD pull joined that into a single line, then the SD push
        // re-split via the trailing-digit heuristic into
        // streetName="Oude Asserweg" + streetNumber="7" + boxNumber="".
        // Both representations encode the same address; the overlay must
        // leave the WK distribution untouched.
        var get = """
        { "name": "Acme",
          "streetName": "Oude Asserweg 7", "streetNumber": null, "boxNumber": null,
          "country": { "id": "11111111-1111-1111-1111-111111111111" } }
        """;
        var payload = PayloadOnlyNameChanged() with
        {
            StreetName = "Oude Asserweg",
            StreetNumber = "7",
            BoxNumber = "",
        };

        var body = Build(get, payload, CustomerId);

        Assert.Equal("Oude Asserweg 7", body.GetProperty("streetName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("streetNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("boxNumber").ValueKind);
    }

    [Fact]
    public void Overlay_writes_split_address_when_recombination_differs()
    {
        // Inverse of the previous test: when the SD user actually edits the
        // address, the recombined line differs and we overlay all three
        // fields with our split. WK's old distribution is replaced.
        var get = """
        { "name": "Acme",
          "streetName": "Old Street 1", "streetNumber": null, "boxNumber": null,
          "country": { "id": "11111111-1111-1111-1111-111111111111" } }
        """;
        var payload = PayloadOnlyNameChanged() with
        {
            StreetName = "New Street",
            StreetNumber = "5",
            BoxNumber = "B",
        };

        var body = Build(get, payload, CustomerId);

        Assert.Equal("New Street", body.GetProperty("streetName").GetString());
        Assert.Equal("5", body.GetProperty("streetNumber").GetString());
        Assert.Equal("B", body.GetProperty("boxNumber").GetString());
    }

    [Fact]
    public void Overlay_treats_box_prefix_consistently_in_recombination()
    {
        // WK stores boxNumber="12" alongside streetName/streetNumber. The
        // SD pull rendered that as "Bus 12" on address_line2; the SD push
        // strips the "Bus " prefix back to "12". Recombination on both
        // sides must produce the same canonical line so the overlay skips.
        var get = """
        { "name": "Acme",
          "streetName": "Main", "streetNumber": "100", "boxNumber": "12",
          "country": { "id": "11111111-1111-1111-1111-111111111111" } }
        """;
        var payload = PayloadOnlyNameChanged() with
        {
            StreetName = "Main",
            StreetNumber = "100",
            BoxNumber = "12",
        };

        var body = Build(get, payload, CustomerId);

        Assert.Equal("Main", body.GetProperty("streetName").GetString());
        Assert.Equal("100", body.GetProperty("streetNumber").GetString());
        Assert.Equal("12", body.GetProperty("boxNumber").GetString());
    }

    [Fact]
    public void Overlay_skips_name_when_payload_equals_get()
    {
        // The full-equal case: SD payload identical to WK row → PUT body
        // identical to GET body (modulo dropped fields + shape transform).
        // No mutation, no lastModified bump, no echo-pull drift.
        var payload = PayloadOnlyNameChanged() with { Name = "Datawolk BV" }; // same as GET

        var body = Build(FullGetShape, payload, CustomerId);

        Assert.Equal("Datawolk BV", body.GetProperty("name").GetString());
        // Email stayed null (GET) instead of "" (payload normalization).
        Assert.Equal(JsonValueKind.Null, body.GetProperty("email").ValueKind);
        // Address kept WK's distribution.
        Assert.Equal("Kaulillerweg", body.GetProperty("streetName").GetString());
        Assert.Equal("4", body.GetProperty("streetNumber").GetString());
    }

    [Fact]
    public void Overlay_normalizes_whitespace_when_comparing()
    {
        // " foo " from payload equals "foo" from GET (both sides trim).
        // We don't want trailing-space typos to register as a real change
        // and trigger a no-op PUT.
        var get = """{ "name": "foo" }""";
        var payload = PayloadOnlyNameChanged() with { Name = " foo " };

        var body = Build(get, payload, CustomerId);

        Assert.Equal("foo", body.GetProperty("name").GetString());
    }

    [Fact]
    public void Overlay_omits_alphaCode_when_payload_empty_so_existing_value_is_not_blanked()
    {
        // alphaCode + number are immutable upstream identifiers. If the local
        // row pre-dates the column being populated (payload value empty) we
        // must not overwrite WK's value with empty — instead keep whatever
        // CopyIfPresent put there from the GET.
        var get = """
        { "name": "Acme", "alphaCode": "ACM999", "number": "999",
          "country": { "id": "11111111-1111-1111-1111-111111111111" } }
        """;
        var payload = PayloadOnlyNameChanged() with { AlphaCode = null, Number = null };

        var body = Build(get, payload, CustomerId);

        Assert.Equal("ACM999", body.GetProperty("alphaCode").GetString());
        Assert.Equal("999", body.GetProperty("number").GetString());
    }

    [Fact]
    public void Overlay_overwrites_alphaCode_and_number_when_payload_has_them()
    {
        // Inverse of the previous test — a payload that does carry the
        // identifiers must overwrite WK's value (round-trip after a CREATE
        // returned a fresh klantnummer).
        var get = """{ "alphaCode": "OLD", "number": "111" }""";
        var payload = PayloadOnlyNameChanged() with { AlphaCode = "NEW", Number = "222" };

        var body = Build(get, payload, CustomerId);

        Assert.Equal("NEW", body.GetProperty("alphaCode").GetString());
        Assert.Equal("222", body.GetProperty("number").GetString());
    }

    [Fact]
    public void Overlay_emits_null_countryId_when_get_country_id_is_null()
    {
        // With the canonical-template approach the slot is always present;
        // when WK has no country reference, we send JSON null. WK's PUT-as-
        // replace then keeps it as null (no upgrade, no downgrade — same
        // state as before).
        var get = """{ "country": { "id": null } }""";

        var body = Build(get, PayloadOnlyNameChanged(), CustomerId);

        Assert.True(body.TryGetProperty("countryId", out var countryId));
        Assert.Equal(JsonValueKind.Null, countryId.ValueKind);
    }

    [Fact]
    public void Overlay_emits_empty_bankAccountLines_when_get_has_no_bankAccounts()
    {
        // Canonical UpdateCustomerRequest always includes the bankAccountLines
        // slot. When GET has no bankAccounts (or an empty array), we emit
        // [] — which mirrors what WK had and keeps the body shape stable.
        var get = """{ "name": "Acme" }""";

        var body = Build(get, PayloadOnlyNameChanged(), CustomerId);

        Assert.True(body.TryGetProperty("bankAccountLines", out var lines));
        Assert.Equal(JsonValueKind.Array, lines.ValueKind);
        Assert.Equal(0, lines.GetArrayLength());
    }

    [Fact]
    public void Overlay_omits_id_field_from_put_body()
    {
        // UpdateCustomerRequest schema does not have an `id` slot — the URL
        // carries the customer id. Sending a stray `id` in the body has been
        // observed correlated with Adsolut's AccountingHarbour import error
        // (POCollection.Populate stale-DataRow exception, 2026-04-30).
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        Assert.False(body.TryGetProperty("id", out _),
            "id is not part of UpdateCustomerRequest — must not appear in PUT body");
    }

    [Fact]
    public void Overlay_emits_active_true_by_default_since_get_has_no_active_field()
    {
        // CustomerDetailResponse does not expose `active`; UpdateCustomerRequest
        // requires it as non-nullable bool. We default to true — every customer
        // we have a GET for is by definition still listed by WK (active).
        var body = Build(FullGetShape, PayloadOnlyNameChanged(), CustomerId);

        Assert.True(body.TryGetProperty("active", out var active));
        Assert.Equal(JsonValueKind.True, active.ValueKind);
    }

    [Fact]
    public void Overlay_emits_every_UpdateCustomerRequest_slot_even_for_empty_get()
    {
        // Spec-pin: the canonical template always emits every slot the
        // schema defines, in the documented order. A GET that's almost
        // empty still produces a full body — slots default to null /
        // empty / sensible default per type.
        var body = Build("""{ "name": "Acme" }""", PayloadOnlyNameChanged(), CustomerId);

        var expected = new[]
        {
            "alphaCode", "number", "dueDays", "dueDate", "name",
            "fax", "phone", "mobilePhone", "email", "postalCode", "city",
            "streetName", "streetNumber", "boxNumber",
            "countryId", "vatNumber", "countryPrefixVatNumber",
            "vatSpecificationId", "vatPercentageId",
            "active", "bankAccountLines", "externalId", "languageIsoCode",
            "financialDiscountPercentage", "financialDiscountDays",
            "remindersEnabled", "remarks",
        };
        foreach (var slot in expected)
        {
            Assert.True(body.TryGetProperty(slot, out _),
                $"PUT body missing UpdateCustomerRequest slot '{slot}'");
        }
    }

    [Fact]
    public void Overlay_throws_AdsolutApiException_on_non_json_body()
    {
        var ex = Assert.Throws<AdsolutApiException>(() =>
            AdsolutCustomersWriteClient.BuildUpdateBody("<html>oops</html>", PayloadOnlyNameChanged(), CustomerId));

        Assert.Equal("pre_update_overlay_bad_json", ex.UpstreamErrorCode);
    }

    [Fact]
    public void Overlay_throws_AdsolutApiException_on_non_object_body()
    {
        var ex = Assert.Throws<AdsolutApiException>(() =>
            AdsolutCustomersWriteClient.BuildUpdateBody("[]", PayloadOnlyNameChanged(), CustomerId));

        Assert.Equal("pre_update_overlay_bad_shape", ex.UpstreamErrorCode);
    }

    // ---- BuildUpdateBody no-overlay path (debug PUT-preview tool) -----
    //
    // The admin debug PUT-preview card calls BuildUpdateBody with a null
    // overlay so the body it shows the admin is "what we'd send right now
    // without applying any local edits". This is the same body our pusher
    // produces when the local row is already in sync (every Overlay* call
    // would hit its no-op branch). Tests pin that:
    //   1. every UpdateCustomerRequest slot is still present, so the body
    //      shape is stable regardless of which path called it,
    //   2. the SD-managed fields take their value from the GET (no payload
    //      overrides leaked through), and
    //   3. read-vs-write transformations still apply (countryId lift,
    //      bankAccountLines rename, drop-list).

    [Fact]
    public void NoOverlay_keeps_GET_managed_field_values()
    {
        var raw = AdsolutCustomersWriteClient.BuildUpdateBody(FullGetShape, overlay: null, CustomerId);
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.Equal("Datawolk BV", body.GetProperty("name").GetString());
        Assert.Equal("Kaulillerweg", body.GetProperty("streetName").GetString());
        Assert.Equal("4", body.GetProperty("streetNumber").GetString());
        Assert.Equal("0740997252", body.GetProperty("vatNumber").GetString());
        Assert.Equal("BE", body.GetProperty("countryPrefixVatNumber").GetString());
        // GET had email = null → PUT slot must be JSON null, not "" — proving
        // the overlay-skip branch ran and SD's null≡"" coercion did not.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("email").ValueKind);
    }

    [Fact]
    public void NoOverlay_still_applies_read_to_write_transformations()
    {
        var raw = AdsolutCustomersWriteClient.BuildUpdateBody(FullGetShape, overlay: null, CustomerId);
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.False(body.TryGetProperty("country", out _));
        Assert.Equal("5c99e5ad-2091-4717-bdd1-c529a8e8934b", body.GetProperty("countryId").GetString());
        Assert.False(body.TryGetProperty("bankAccounts", out _));
        Assert.Equal(JsonValueKind.Array, body.GetProperty("bankAccountLines").ValueKind);
        Assert.False(body.TryGetProperty("code", out _));
        Assert.False(body.TryGetProperty("id", out _));
        Assert.False(body.TryGetProperty("lastModified", out _));
    }

    [Fact]
    public void NoOverlay_emits_every_canonical_slot()
    {
        var raw = AdsolutCustomersWriteClient.BuildUpdateBody(FullGetShape, overlay: null, CustomerId);
        var body = JsonDocument.Parse(raw).RootElement;

        var expected = new[]
        {
            "alphaCode", "number", "dueDays", "dueDate", "name",
            "fax", "phone", "mobilePhone", "email", "postalCode", "city",
            "streetName", "streetNumber", "boxNumber",
            "countryId", "vatNumber", "countryPrefixVatNumber",
            "vatSpecificationId", "vatPercentageId",
            "active", "bankAccountLines", "externalId", "languageIsoCode",
            "financialDiscountPercentage", "financialDiscountDays",
            "remindersEnabled", "remarks",
        };
        foreach (var slot in expected)
        {
            Assert.True(body.TryGetProperty(slot, out _),
                $"PUT body missing UpdateCustomerRequest slot '{slot}'");
        }
    }
}
