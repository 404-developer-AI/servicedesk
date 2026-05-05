using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.28 — pins the response-parser of the contacts read-client. The
/// HTTP path is reviewer-trusted; this guards the body-shape tolerance
/// (bare array vs documented paged-result wrapper), the field-mapping
/// (id/firstName/lastName/email/phone/mobilePhone/active/lastModified)
/// and the offset-less <c>lastModified</c> canary that warns when WK
/// drops the explicit timezone suffix.
public sealed class AdsolutContactsClientParseTests
{
    [Fact]
    public void Empty_body_yields_empty_list()
    {
        var r = AdsolutContactsClient.ParseContacts(string.Empty);

        Assert.Empty(r.Contacts);
        Assert.Equal(0, r.OffsetlessLastModifiedCount);
    }

    [Fact]
    public void Whitespace_only_body_yields_empty_list()
    {
        var r = AdsolutContactsClient.ParseContacts("   \r\n  ");

        Assert.Empty(r.Contacts);
        Assert.Equal(0, r.OffsetlessLastModifiedCount);
    }

    [Fact]
    public void Bare_array_with_one_contact_parses_all_mirrored_fields()
    {
        // Shape per Adsolut OpenAPI for /customers/{id}/contacts: bare
        // array of CustomerContactResponse objects.
        var body = """
        [
          {
            "id": "11111111-1111-1111-1111-111111111111",
            "firstName": "Wendy",
            "lastName": "Janssens",
            "email": "wendy@example.be",
            "phone": "+32 9 123 45 67",
            "mobilePhone": "+32 478 12 34 56",
            "active": true,
            "fax": "should be ignored",
            "memo": "should be ignored",
            "lastModified": "2026-04-30T10:07:04+00:00"
          }
        ]
        """;

        var r = AdsolutContactsClient.ParseContacts(body);

        Assert.Single(r.Contacts);
        var c = r.Contacts[0];
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), c.Id);
        Assert.Equal("Wendy", c.FirstName);
        Assert.Equal("Janssens", c.LastName);
        Assert.Equal("wendy@example.be", c.Email);
        Assert.Equal("+32 9 123 45 67", c.Phone);
        Assert.Equal("+32 478 12 34 56", c.MobilePhone);
        Assert.True(c.Active);
        Assert.NotNull(c.LastModified);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 30, 10, 7, 4, TimeSpan.Zero),
            c.LastModified!.Value);
        Assert.Equal(0, r.OffsetlessLastModifiedCount);
    }

    [Fact]
    public void Paged_result_envelope_is_also_accepted()
    {
        // Defensive parser fallback: if WK ever wraps the contacts list in
        // their standard `{ items: [...] }` envelope, we still extract
        // rows correctly.
        var body = """
        {
          "currentPage": 1,
          "totalItems": 1,
          "totalPages": 1,
          "items": [
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "firstName": "Jan",
              "lastName": "Peeters",
              "email": "jan@example.be",
              "active": false,
              "lastModified": "2026-05-01T08:00:00+00:00"
            }
          ]
        }
        """;

        var r = AdsolutContactsClient.ParseContacts(body);

        Assert.Single(r.Contacts);
        Assert.Equal("Jan", r.Contacts[0].FirstName);
        Assert.False(r.Contacts[0].Active);
    }

    [Fact]
    public void Missing_id_skips_the_row()
    {
        // Defensive: a malformed row without id can't be tracked at the
        // link layer. Skip it instead of throwing.
        var body = """
        [
          { "firstName": "No-ID Contact", "email": "x@y.z" },
          {
            "id": "33333333-3333-3333-3333-333333333333",
            "firstName": "Valid",
            "email": "v@y.z",
            "active": true
          }
        ]
        """;

        var r = AdsolutContactsClient.ParseContacts(body);

        Assert.Single(r.Contacts);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), r.Contacts[0].Id);
    }

    [Fact]
    public void Active_defaults_to_true_when_field_absent()
    {
        // Adsolut's schema marks `active` as required, but be defensive:
        // default true so an absent field doesn't deactivate by accident.
        var body = """
        [
          {
            "id": "44444444-4444-4444-4444-444444444444",
            "email": "w@x.y"
          }
        ]
        """;

        var r = AdsolutContactsClient.ParseContacts(body);

        Assert.Single(r.Contacts);
        Assert.True(r.Contacts[0].Active);
    }

    [Fact]
    public void Null_string_fields_become_empty_strings()
    {
        // SD's schema uses NOT NULL DEFAULT '' on the mirrored text columns;
        // the parser normalises null → empty so the upserter doesn't have
        // to special-case nullable strings.
        var body = """
        [
          {
            "id": "55555555-5555-5555-5555-555555555555",
            "firstName": null,
            "lastName": null,
            "email": "x@x.x",
            "phone": null,
            "mobilePhone": null,
            "active": true
          }
        ]
        """;

        var r = AdsolutContactsClient.ParseContacts(body);

        Assert.Single(r.Contacts);
        Assert.Equal(string.Empty, r.Contacts[0].FirstName);
        Assert.Equal(string.Empty, r.Contacts[0].LastName);
        Assert.Equal(string.Empty, r.Contacts[0].Phone);
        Assert.Equal(string.Empty, r.Contacts[0].MobilePhone);
    }

    [Fact]
    public void Offsetless_lastModified_increments_canary_counter()
    {
        var body = """
        [
          {
            "id": "66666666-6666-6666-6666-666666666666",
            "email": "x@x.x",
            "active": true,
            "lastModified": "2026-04-30T10:07:04"
          }
        ]
        """;

        var r = AdsolutContactsClient.ParseContacts(body);

        Assert.Single(r.Contacts);
        Assert.Equal(1, r.OffsetlessLastModifiedCount);
    }

    [Fact]
    public void Garbage_body_throws_JsonException()
    {
        // A 200 response that isn't JSON does throw — this matches
        // AdsolutCustomersClient behaviour and is caught at the worker
        // level (one error row in integration_audit, no advance of the
        // delta cursor, retry next tick).
        // JsonReaderException derives from JsonException; assert the base
        // type so a future tightening of the JSON parser doesn't break us.
        Assert.ThrowsAny<global::System.Text.Json.JsonException>(() =>
            AdsolutContactsClient.ParseContacts("this is not json"));
    }
}
