using Servicedesk.Infrastructure.Integrations.Telavox;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.34 — pins the response-parsers of <see cref="TelavoxApiClient"/>.
/// The HTTP transport itself (bearer attach, audit row writing, status
/// mapping) is reviewer-trusted; these tests guard the body-shape tolerance
/// across the four parsers that translate Telavox JSON into SD records.
/// All parsers must:
/// <list type="bullet">
/// <item>Accept either a bare JSON array or an envelope wrapping
/// <c>items</c> / <c>data</c>.</item>
/// <item>Skip rows missing a primary key (id / callId) rather than throw.</item>
/// <item>Treat missing nullable string fields as null, missing required
/// strings as empty.</item>
/// <item>Return an empty collection for empty / whitespace input.</item>
/// </list>
public sealed class TelavoxApiClientParseTests
{
    // ---- ParseCustomers ----

    [Fact]
    public void ParseCustomers_empty_body_yields_empty_list()
    {
        Assert.Empty(TelavoxApiClient.ParseCustomers(string.Empty));
        Assert.Empty(TelavoxApiClient.ParseCustomers("   \n\t  "));
    }

    [Fact]
    public void ParseCustomers_bare_array_parses_key_and_name()
    {
        // PAPI swagger CustomerDto: identifier lives under `key`,
        // example "customer-123". Pre-D the parser looked for `id` only
        // and silently dropped every row.
        var body = """
        [
          { "key": "customer-1", "name": "Acme NV" },
          { "key": "customer-2", "name": "Globex" }
        ]
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Equal(2, list.Count);
        Assert.Equal("customer-1", list[0].Id);
        Assert.Equal("Acme NV", list[0].Name);
        Assert.Equal("customer-2", list[1].Id);
    }

    [Fact]
    public void ParseCustomers_falls_back_to_id_when_key_missing()
    {
        // Defensive fallback so a partner-environment variant with the
        // older `id` field still populates the dropdown. Never preferred
        // over `key`.
        var body = """
        [ { "id": "cust-legacy", "name": "Legacy Customer" } ]
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("cust-legacy", list[0].Id);
    }

    [Fact]
    public void ParseCustomers_paged_envelope_under_items_is_accepted()
    {
        var body = """
        {
          "currentPage": 1,
          "items": [ { "key": "customer-1", "name": "Acme" } ]
        }
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("Acme", list[0].Name);
    }

    [Fact]
    public void ParseCustomers_data_envelope_also_accepted()
    {
        var body = """
        { "data": [ { "key": "customer-3", "name": "Initech" } ] }
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("customer-3", list[0].Id);
    }

    [Fact]
    public void ParseCustomers_row_without_key_or_id_is_skipped()
    {
        var body = """
        [
          { "name": "Missing key" },
          { "key": "customer-1", "name": "Real" }
        ]
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("customer-1", list[0].Id);
    }

    [Fact]
    public void ParseCustomers_malformed_json_returns_empty_not_throws()
    {
        // A 200 with garbage body must not crash — the API client treats
        // an empty list as "nothing returned" and the admin sees an empty
        // dropdown (not a 502).
        Assert.Empty(TelavoxApiClient.ParseCustomers("not json"));
    }

    // ---- ParseExtensions ----

    [Fact]
    public void ParseExtensions_canonical_papi_shape_is_parsed()
    {
        // PAPI swagger ExtensionDto: identifier under `key`, dialable
        // number lives under fixedNumber.e164Number / mobileNumber.e164Number,
        // email is flat (no nested user). The key is what the CAPI
        // /v1/extensions/{extension}/calls path-param takes — NOT the
        // dialable number.
        var body = """
        [
          {
            "key": "extension-100",
            "name": "Reception",
            "email": "reception@example.be",
            "fixedNumber": { "key": "phone-1", "e164Number": "+3290011100" }
          },
          {
            "key": "extension-101",
            "name": "Alice",
            "email": "alice@example.be",
            "mobileNumber": { "key": "phone-2", "e164Number": "+32498123456" }
          }
        ]
        """;
        var list = TelavoxApiClient.ParseExtensions(body);
        Assert.Equal(2, list.Count);
        Assert.Equal("extension-100", list[0].Id);
        Assert.Equal("+3290011100", list[0].Number);
        Assert.Equal("Reception", list[0].Name);
        Assert.Equal("reception@example.be", list[0].UserEmail);
        Assert.Equal("extension-101", list[1].Id);
        Assert.Equal("+32498123456", list[1].Number);
    }

    [Fact]
    public void ParseExtensions_prefers_fixedNumber_over_mobileNumber()
    {
        // When both are present the fixed (desk) number is the more
        // recognisable label for an admin.
        var body = """
        [ {
          "key": "extension-200",
          "fixedNumber": { "e164Number": "+3290022200" },
          "mobileNumber": { "e164Number": "+32498200200" }
        } ]
        """;
        var list = TelavoxApiClient.ParseExtensions(body);
        Assert.Single(list);
        Assert.Equal("+3290022200", list[0].Number);
    }

    [Fact]
    public void ParseExtensions_falls_back_to_legacy_number_field()
    {
        // Defensive: a non-canonical environment might still expose a
        // flat "number" field; we accept it so the dropdown isn't empty.
        var body = """
        [ { "key": "extension-1", "number": "201" } ]
        """;
        var list = TelavoxApiClient.ParseExtensions(body);
        Assert.Single(list);
        Assert.Equal("201", list[0].Number);
    }

    [Fact]
    public void ParseExtensions_no_phone_numbers_yields_empty_string()
    {
        var body = """
        [ { "key": "extension-x", "name": "Orphan" } ]
        """;
        var list = TelavoxApiClient.ParseExtensions(body);
        Assert.Single(list);
        Assert.Equal(string.Empty, list[0].Number);
        Assert.Equal("Orphan", list[0].Name);
    }

    // ---- ParseApiUserKey (POST /api-users response) ----

    [Fact]
    public void ParseApiUserKey_canonical_shape_lifts_key()
    {
        // PAPI swagger: ApiUserDto carries `key`, `name`, `tokens[]`, `links[]`.
        // We only need the key — the bearer-token comes from the second
        // POST in the two-step flow.
        var body = """
        {
          "key": "apiUser-9001",
          "name": "sd-agent-abc-1234567890",
          "tokens": [],
          "links": []
        }
        """;
        Assert.Equal("apiUser-9001", TelavoxApiClient.ParseApiUserKey(body));
    }

    [Fact]
    public void ParseApiUserKey_missing_key_returns_empty()
    {
        // No key → empty string. The client layer turns that into a
        // structured TelavoxApiException so the admin sees "did not carry
        // a key field" rather than a NullReferenceException.
        var body = """
        { "name": "sd-agent-noop" }
        """;
        Assert.Equal(string.Empty, TelavoxApiClient.ParseApiUserKey(body));
    }

    [Fact]
    public void ParseApiUserKey_empty_or_garbage_returns_empty()
    {
        Assert.Equal(string.Empty, TelavoxApiClient.ParseApiUserKey(string.Empty));
        Assert.Equal(string.Empty, TelavoxApiClient.ParseApiUserKey("  "));
        Assert.Equal(string.Empty, TelavoxApiClient.ParseApiUserKey("not json"));
        Assert.Equal(string.Empty, TelavoxApiClient.ParseApiUserKey("[1,2,3]"));
    }

    // ---- ParseCapiTokenBearer (POST /api-users/{key}/tokens response) ----

    [Fact]
    public void ParseCapiTokenBearer_canonical_shape_lifts_bearerToken()
    {
        // PAPI swagger: CapiTokenDto carries `key`, `bearerToken`,
        // `invalidationDate`, `links[]`. Bearer is what the worker
        // attaches to every CAPI request.
        var body = """
        {
          "key": "apiToken-abcd",
          "bearerToken": "ey.SHARP.SECRET",
          "invalidationDate": "2027-01-01T00:00:00"
        }
        """;
        Assert.Equal("ey.SHARP.SECRET", TelavoxApiClient.ParseCapiTokenBearer(body));
    }

    [Fact]
    public void ParseCapiTokenBearer_missing_field_returns_empty()
    {
        // Same belt-and-braces as ParseApiUserKey: empty triggers the
        // structured 502-style error rather than letting an empty bearer
        // slip into protected_secrets.
        var body = """
        { "key": "apiToken-noop" }
        """;
        Assert.Equal(string.Empty, TelavoxApiClient.ParseCapiTokenBearer(body));
    }

    [Fact]
    public void ParseCapiTokenBearer_empty_or_garbage_returns_empty()
    {
        Assert.Equal(string.Empty, TelavoxApiClient.ParseCapiTokenBearer(string.Empty));
        Assert.Equal(string.Empty, TelavoxApiClient.ParseCapiTokenBearer("not-json"));
    }

    // ---- ParseCurrentCall (CAPI OngoingCallDto[]) ----

    [Fact]
    public void ParseCurrentCall_empty_body_returns_null()
    {
        Assert.Null(TelavoxApiClient.ParseCurrentCall(string.Empty));
        Assert.Null(TelavoxApiClient.ParseCurrentCall("   "));
    }

    [Fact]
    public void ParseCurrentCall_empty_array_returns_null()
    {
        Assert.Null(TelavoxApiClient.ParseCurrentCall("[]"));
    }

    [Fact]
    public void ParseCurrentCall_canonical_capi_shape_is_parsed()
    {
        // CAPI swagger OngoingCallDto: { callerId, callDirection, lineStatus }.
        // No callId — the parser synthesises one from callerId so the
        // state-machine can dedup same-call ticks. State is lower-cased
        // verbatim so transition rules can match the CAPI vocab.
        var body = """
        [
          {
            "callerId": "0032473584015",
            "callDirection": "incoming",
            "lineStatus": "ringing"
          }
        ]
        """;
        var call = TelavoxApiClient.ParseCurrentCall(body);
        Assert.NotNull(call);
        Assert.Equal("0032473584015", call!.CallId);
        Assert.Equal("ringing", call.State);
        Assert.Equal("0032473584015", call.FromNumber);
        Assert.Null(call.ToNumber);
        Assert.Null(call.StartUtc);
        Assert.Equal("incoming", call.Direction);
    }

    [Fact]
    public void ParseCurrentCall_duplicate_rows_during_ringing_pick_first()
    {
        // Empirically CAPI returns one row per terminal/device — the same
        // call surfaces 2-3 times during ringing. The state-machine
        // debounces same-state ticks; we just take the first valid row so
        // the worker stays simple.
        var body = """
        [
          { "callerId": "0032473584015", "callDirection": "incoming", "lineStatus": "ringing" },
          { "callerId": "0032473584015", "callDirection": "incoming", "lineStatus": "ringing" }
        ]
        """;
        var call = TelavoxApiClient.ParseCurrentCall(body);
        Assert.NotNull(call);
        Assert.Equal("ringing", call!.State);
    }

    [Fact]
    public void ParseCurrentCall_answered_call_has_up_lineStatus()
    {
        var body = """
        [ { "callerId": "0032473584015", "callDirection": "incoming", "lineStatus": "up" } ]
        """;
        var call = TelavoxApiClient.ParseCurrentCall(body);
        Assert.NotNull(call);
        Assert.Equal("up", call!.State);
    }

    [Fact]
    public void ParseCurrentCall_outgoing_call_is_parsed_with_direction()
    {
        // The parser no longer drops outbound rows — it carries the
        // direction so the worker can keep the popup inbound-only (gated in
        // TelavoxCallTransition) while the dashboard call-state indicator
        // still tracks an agent dialling out.
        var body = """
        [ { "callerId": "0032473584015", "callDirection": "outgoing", "lineStatus": "up" } ]
        """;
        var call = TelavoxApiClient.ParseCurrentCall(body);
        Assert.NotNull(call);
        Assert.Equal("up", call!.State);
        Assert.Equal("outgoing", call.Direction);
    }

    [Fact]
    public void ParseCurrentCall_down_lineStatus_is_skipped()
    {
        // "down" is the terminal state — CAPI keeps returning the row for
        // a tick after hangup. Skipping it lets the worker treat it as
        // "no active call" and clear the baseline cleanly.
        var body = """
        [ { "callerId": "0032473584015", "callDirection": "incoming", "lineStatus": "down" } ]
        """;
        Assert.Null(TelavoxApiClient.ParseCurrentCall(body));
    }

    [Fact]
    public void ParseCurrentCall_row_without_callerId_is_skipped()
    {
        var body = """
        [ { "callDirection": "incoming", "lineStatus": "ringing" } ]
        """;
        Assert.Null(TelavoxApiClient.ParseCurrentCall(body));
    }

    [Fact]
    public void ParseCurrentCall_envelope_under_items_or_data_still_accepted()
    {
        // Defensive: if Telavox ever wraps the array we still parse it,
        // so a tenant-side schema change doesn't silently break the popup.
        var itemsBody = """
        { "items": [ { "callerId": "0032111", "callDirection": "incoming", "lineStatus": "ringing" } ] }
        """;
        var call = TelavoxApiClient.ParseCurrentCall(itemsBody);
        Assert.NotNull(call);
        Assert.Equal("0032111", call!.CallId);

        var dataBody = """
        { "data": [ { "callerId": "0032222", "callDirection": "incoming", "lineStatus": "ringing" } ] }
        """;
        Assert.Equal("0032222", TelavoxApiClient.ParseCurrentCall(dataBody)?.CallId);
    }

    [Fact]
    public void ParseCurrentCall_malformed_json_returns_null_not_throws()
    {
        Assert.Null(TelavoxApiClient.ParseCurrentCall("not-json"));
    }
}
