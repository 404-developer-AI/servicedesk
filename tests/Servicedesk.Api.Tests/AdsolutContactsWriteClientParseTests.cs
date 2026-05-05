using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.29 — pins the response-parser + read-modify-write body construction
/// of the contacts write-client. The HTTP path is reviewer-trusted; these
/// tests guard:
/// <list type="bullet">
/// <item>Body-shape tolerance: empty / whitespace / id-only echo / full
/// row / lowercase Z offset / non-object / non-JSON each route to a
/// well-defined result so the caller can fall back without try/catch
/// gymnastics.</item>
/// <item>Canonical PUT body shape: every <c>UpdateCustomerContactRequest</c>
/// slot present in the documented order; nested <c>{country: {id}}</c>
/// transformed to flat <c>countryId</c>; non-nullable bools defaulted.</item>
/// <item>Managed overlay (firstName, lastName, phone, mobilePhone) writes
/// only when the payload value differs semantically from what WK already
/// has — null↔"" treated as equal, trim-equal treated as equal — so a
/// no-edit round-trip produces a body identical to what WK already has,
/// keeping <c>lastModified</c> stable.</item>
/// <item>Non-managed fields (fax, memo, address, the four for-flags, …)
/// are preserved verbatim: a PUT with no overlay equals the GET shape.</item>
/// <item><c>name</c> required-field rule: fall back to
/// <c>firstName + " " + lastName</c> when WK had no name; never overwrite
/// a non-empty WK name (it's Adsolut's display name and may carry
/// admin-typed formatting).</item>
/// </list>
public sealed class AdsolutContactsWriteClientParseTests
{
    // ---- ParseWriteResponse -----------------------------------------

    [Fact]
    public void Empty_body_yields_empty_id_and_null_lastModified()
    {
        var r = AdsolutContactsWriteClient.ParseWriteResponse(string.Empty);

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Whitespace_only_body_yields_empty_id_and_null_lastModified()
    {
        var r = AdsolutContactsWriteClient.ParseWriteResponse("   \r\n  ");

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Slim_id_only_echo_parses_id_and_leaves_lastModified_null()
    {
        // POST /contacts returns AddEntityWithValidationResponse, which is
        // exactly { id, validationResults } — no lastModified. We need to
        // parse the id so the pusher can trigger a read-back.
        var body = """{"id":"22222222-2222-2222-2222-222222222222","validationResults":[]}""";
        var r = AdsolutContactsWriteClient.ParseWriteResponse(body);

        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Full_row_echo_parses_both_id_and_lastModified()
    {
        var body = """
        {
          "id": "33333333-3333-3333-3333-333333333333",
          "name": "Wendy De Smet",
          "lastModified": "2026-05-05T11:44:20+00:00"
        }
        """;
        var r = AdsolutContactsWriteClient.ParseWriteResponse(body);

        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), r.Id);
        Assert.NotNull(r.LastModified);
        Assert.Equal(
            new DateTimeOffset(2026, 5, 5, 11, 44, 20, TimeSpan.Zero),
            r.LastModified!.Value);
    }

    [Fact]
    public void Lowercase_z_offset_parses()
    {
        var body = """{"id":"44444444-4444-4444-4444-444444444444","lastModified":"2026-05-05T11:44:20Z"}""";
        var r = AdsolutContactsWriteClient.ParseWriteResponse(body);

        Assert.NotNull(r.LastModified);
        Assert.Equal(
            new DateTimeOffset(2026, 5, 5, 11, 44, 20, TimeSpan.Zero),
            r.LastModified!.Value);
    }

    [Fact]
    public void Non_object_body_yields_empty_id_and_null_lastModified()
    {
        // Not a documented shape, but we want to be defensive — a future
        // WK change to a bare-array response shouldn't crash the worker.
        var r = AdsolutContactsWriteClient.ParseWriteResponse("[1,2,3]");

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    [Fact]
    public void Non_json_body_yields_empty_id_and_null_lastModified()
    {
        // 502 Bad Gateway HTML page or a CDN intercept — never throw.
        var r = AdsolutContactsWriteClient.ParseWriteResponse("<html>nope</html>");

        Assert.Equal(Guid.Empty, r.Id);
        Assert.Null(r.LastModified);
    }

    // ---- BuildUpdateBody --------------------------------------------

    private const string FullGetBody = """
        {
          "name": "Wendy De Smet",
          "active": true,
          "languageIsoCode": "nl",
          "firstName": "Wendy",
          "lastName": "De Smet",
          "address": "Rue de la Loi 1",
          "city": "Brussels",
          "postalCode": "1000",
          "country": { "id": "00000000-0000-0000-0000-aaaaaaaaaaaa" },
          "email": "wendy@acme.example",
          "phone": "+32 2 111 22 33",
          "mobilePhone": "+32 470 11 22 33",
          "fax": "+32 2 999 88 77",
          "memo": "VIP",
          "dateOfBirth": "1985-04-12",
          "nationalIdentificationNumber": "85.04.12-001.23",
          "forCommunication": true,
          "forOrderConfirmations": false,
          "forPaymentReminders": true,
          "forInvoiceMails": true,
          "deleteRequestIsPending": false,
          "id": "55555555-5555-5555-5555-555555555555",
          "lastModified": "2026-05-05T11:44:20+00:00"
        }
        """;

    private static AdsolutContactWritePayload Overlay(
        string firstName = "Wendy",
        string lastName = "De Smet",
        string phone = "+32 2 111 22 33",
        string mobilePhone = "+32 470 11 22 33",
        string email = "wendy@acme.example") =>
        new(firstName, lastName, phone, mobilePhone, email);

    [Fact]
    public void BuildUpdateBody_no_overlay_path_keeps_GET_values()
    {
        var body = AdsolutContactsWriteClient.BuildUpdateBody(FullGetBody, overlay: null,
            contactId: Guid.Parse("55555555-5555-5555-5555-555555555555"));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Wendy", root.GetProperty("firstName").GetString());
        Assert.Equal("De Smet", root.GetProperty("lastName").GetString());
        Assert.Equal("+32 2 111 22 33", root.GetProperty("phone").GetString());
        Assert.Equal("+32 470 11 22 33", root.GetProperty("mobilePhone").GetString());
        Assert.Equal("Wendy De Smet", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("active").GetBoolean());
        Assert.Equal("nl", root.GetProperty("languageIsoCode").GetString());
    }

    [Fact]
    public void BuildUpdateBody_lifts_country_to_countryId_uuid()
    {
        // GET shape: { "country": { "id": "..." } }; PUT shape: flat
        // "countryId": "...". Same lift used by the customers write-client.
        var body = AdsolutContactsWriteClient.BuildUpdateBody(FullGetBody, overlay: null,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("country", out _),
            "PUT body should not contain the nested `country` object — only `countryId`.");
        Assert.Equal("00000000-0000-0000-0000-aaaaaaaaaaaa",
            root.GetProperty("countryId").GetString());
    }

    [Fact]
    public void BuildUpdateBody_drops_GET_only_fields()
    {
        // id, lastModified, deleteRequestIsPending must NOT leak into the
        // PUT body — the schema's additionalProperties: false will reject
        // them upstream and even if it didn't, they're not part of
        // UpdateCustomerContactRequest.
        var body = AdsolutContactsWriteClient.BuildUpdateBody(FullGetBody, overlay: null,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("lastModified", out _));
        Assert.False(root.TryGetProperty("deleteRequestIsPending", out _));
    }

    [Fact]
    public void BuildUpdateBody_preserves_unmanaged_fields_verbatim()
    {
        // SD never edits fax / memo / address / the four for-flags / dateOfBirth /
        // nationalIdentificationNumber / languageIsoCode / externalId —
        // they round-trip exactly from GET to PUT.
        var body = AdsolutContactsWriteClient.BuildUpdateBody(FullGetBody, overlay: null,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("+32 2 999 88 77", root.GetProperty("fax").GetString());
        Assert.Equal("VIP", root.GetProperty("memo").GetString());
        Assert.Equal("Rue de la Loi 1", root.GetProperty("address").GetString());
        Assert.Equal("1985-04-12", root.GetProperty("dateOfBirth").GetString());
        Assert.True(root.GetProperty("forCommunication").GetBoolean());
        Assert.False(root.GetProperty("forOrderConfirmations").GetBoolean());
        Assert.True(root.GetProperty("forPaymentReminders").GetBoolean());
        Assert.True(root.GetProperty("forInvoiceMails").GetBoolean());
    }

    [Fact]
    public void BuildUpdateBody_overlay_overwrites_changed_managed_fields()
    {
        var overlay = Overlay(firstName: "Wendy", lastName: "De Smet",
            phone: "+32 2 999 99 99",   // changed
            mobilePhone: "+32 470 11 22 33");

        var body = AdsolutContactsWriteClient.BuildUpdateBody(FullGetBody, overlay,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("+32 2 999 99 99", root.GetProperty("phone").GetString());
        // unchanged fields preserved
        Assert.Equal("Wendy", root.GetProperty("firstName").GetString());
        Assert.Equal("De Smet", root.GetProperty("lastName").GetString());
        Assert.Equal("+32 470 11 22 33", root.GetProperty("mobilePhone").GetString());
    }

    [Fact]
    public void BuildUpdateBody_overlay_with_equal_values_keeps_GET_values()
    {
        // No-op echo path — the four overlay values match GET semantically.
        // Result must equal the no-overlay path so a no-edit round-trip
        // does not bump WK's lastModified.
        var overlay = Overlay();

        var withOverlay = AdsolutContactsWriteClient.BuildUpdateBody(FullGetBody, overlay,
            contactId: Guid.NewGuid());
        var withoutOverlay = AdsolutContactsWriteClient.BuildUpdateBody(FullGetBody, overlay: null,
            contactId: Guid.NewGuid());

        Assert.Equal(withoutOverlay, withOverlay);
    }

    [Fact]
    public void BuildUpdateBody_treats_null_and_empty_as_semantic_equal()
    {
        // GET has phone=null, overlay sends phone="" — the overlay should
        // not write since the two are semantically equivalent. Keeps WK's
        // canonical null-value in place.
        const string getBody = """
            {
              "name": "Test Contact",
              "firstName": "Test",
              "lastName": "Contact",
              "phone": null,
              "mobilePhone": null,
              "active": true,
              "forCommunication": false,
              "forOrderConfirmations": false,
              "forPaymentReminders": false,
              "forInvoiceMails": false
            }
            """;
        var overlay = Overlay(firstName: "Test", lastName: "Contact",
            phone: string.Empty, mobilePhone: string.Empty);

        var body = AdsolutContactsWriteClient.BuildUpdateBody(getBody, overlay,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // phone slot is present with WK's null — not overwritten with ""
        Assert.Equal(JsonValueKind.Null, root.GetProperty("phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("mobilePhone").ValueKind);
    }

    [Fact]
    public void BuildUpdateBody_synthesises_name_when_GET_has_none()
    {
        // The schema requires `name` (minLength: 1). When WK returns
        // a contact with no name (rare but possible after a partial
        // import), we fall back to firstName + " " + lastName so the
        // PUT does not 400.
        const string getBody = """
            {
              "name": null,
              "firstName": "Wendy",
              "lastName": "De Smet",
              "active": true,
              "forCommunication": false,
              "forOrderConfirmations": false,
              "forPaymentReminders": false,
              "forInvoiceMails": false
            }
            """;

        var body = AdsolutContactsWriteClient.BuildUpdateBody(getBody, overlay: null,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Wendy De Smet", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void BuildUpdateBody_does_not_overwrite_non_empty_WK_name()
    {
        // WK had "Wendy 'The VIP' De Smet" — admin-typed formatting we
        // shouldn't reformat. The synthesised fallback must only fire when
        // WK had no name.
        const string getBody = """
            {
              "name": "Wendy 'The VIP' De Smet",
              "firstName": "Wendy",
              "lastName": "De Smet",
              "active": true,
              "forCommunication": false,
              "forOrderConfirmations": false,
              "forPaymentReminders": false,
              "forInvoiceMails": false
            }
            """;

        var body = AdsolutContactsWriteClient.BuildUpdateBody(getBody, overlay: null,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Wendy 'The VIP' De Smet", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void BuildUpdateBody_emits_all_canonical_slots()
    {
        // Pin the slot-completeness contract: every field on the
        // UpdateCustomerContactRequest schema is present in the body
        // (with a sensible default if GET didn't have it).
        const string minimalGet = """
            {
              "name": "Min",
              "firstName": "Min",
              "lastName": "",
              "active": true,
              "forCommunication": false,
              "forOrderConfirmations": false,
              "forPaymentReminders": false,
              "forInvoiceMails": false
            }
            """;

        var body = AdsolutContactsWriteClient.BuildUpdateBody(minimalGet, overlay: null,
            contactId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // All slots from the schema must be present (some null, some defaulted).
        string[] expectedSlots =
        {
            "name", "active", "languageIsoCode", "firstName", "lastName",
            "address", "city", "postalCode", "countryId",
            "phone", "mobilePhone", "email", "fax", "memo",
            "dateOfBirth", "nationalIdentificationNumber",
            "forCommunication", "forOrderConfirmations",
            "forPaymentReminders", "forInvoiceMails",
            "externalId",
        };
        foreach (var slot in expectedSlots)
        {
            Assert.True(root.TryGetProperty(slot, out _),
                $"PUT body missing canonical slot '{slot}'.");
        }
    }

    [Fact]
    public void BuildUpdateBody_throws_AdsolutApiException_on_non_object_body()
    {
        var ex = Assert.Throws<AdsolutApiException>(() =>
            AdsolutContactsWriteClient.BuildUpdateBody("[1,2,3]", overlay: null, contactId: Guid.NewGuid()));
        Assert.Equal("pre_update_overlay_bad_shape", ex.UpstreamErrorCode);
    }

    [Fact]
    public void BuildUpdateBody_throws_AdsolutApiException_on_non_json_body()
    {
        var ex = Assert.Throws<AdsolutApiException>(() =>
            AdsolutContactsWriteClient.BuildUpdateBody("<html>nope</html>", overlay: null, contactId: Guid.NewGuid()));
        Assert.Equal("pre_update_overlay_bad_json", ex.UpstreamErrorCode);
    }
}
