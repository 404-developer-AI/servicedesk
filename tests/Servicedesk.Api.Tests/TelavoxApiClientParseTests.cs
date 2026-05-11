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
    public void ParseCustomers_bare_array_parses_id_and_name()
    {
        var body = """
        [
          { "id": "cust-1", "name": "Acme NV" },
          { "id": "cust-2", "name": "Globex" }
        ]
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Equal(2, list.Count);
        Assert.Equal("cust-1", list[0].Id);
        Assert.Equal("Acme NV", list[0].Name);
        Assert.Equal("cust-2", list[1].Id);
    }

    [Fact]
    public void ParseCustomers_paged_envelope_under_items_is_accepted()
    {
        var body = """
        {
          "currentPage": 1,
          "items": [ { "id": "cust-1", "name": "Acme" } ]
        }
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("Acme", list[0].Name);
    }

    [Fact]
    public void ParseCustomers_data_envelope_also_accepted()
    {
        // Some Telavox responses wrap under "data" rather than "items".
        var body = """
        { "data": [ { "id": "cust-3", "name": "Initech" } ] }
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("cust-3", list[0].Id);
    }

    [Fact]
    public void ParseCustomers_numeric_id_is_stringified()
    {
        // Defensive: Telavox sometimes returns integer ids on the partner
        // API. We normalise to a string so the SD-side settings/dropdown
        // can use one type.
        var body = """
        [ { "id": 4711, "name": "Acme" } ]
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("4711", list[0].Id);
    }

    [Fact]
    public void ParseCustomers_row_without_id_is_skipped()
    {
        var body = """
        [
          { "name": "Missing ID" },
          { "id": "cust-1", "name": "Real" }
        ]
        """;
        var list = TelavoxApiClient.ParseCustomers(body);
        Assert.Single(list);
        Assert.Equal("cust-1", list[0].Id);
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
    public void ParseExtensions_canonical_shape_is_parsed()
    {
        var body = """
        [
          {
            "id": "ext-100",
            "number": "100",
            "name": "Reception",
            "userEmail": "reception@example.be"
          },
          {
            "id": "ext-101",
            "number": "101",
            "user": { "email": "alice@example.be" }
          }
        ]
        """;
        var list = TelavoxApiClient.ParseExtensions(body);
        Assert.Equal(2, list.Count);
        Assert.Equal("100", list[0].Number);
        Assert.Equal("Reception", list[0].Name);
        Assert.Equal("reception@example.be", list[0].UserEmail);
        Assert.Null(list[1].Name);
        Assert.Equal("alice@example.be", list[1].UserEmail);
    }

    [Fact]
    public void ParseExtensions_falls_back_to_extension_field_for_number()
    {
        // Some shapes use "extension" instead of "number" for the dialable
        // value. The parser accepts both so the SD dropdown shows a label
        // regardless of which Telavox shape the install sees.
        var body = """
        [ { "id": "ext-1", "extension": "201" } ]
        """;
        var list = TelavoxApiClient.ParseExtensions(body);
        Assert.Single(list);
        Assert.Equal("201", list[0].Number);
    }

    [Fact]
    public void ParseExtensions_missing_number_becomes_empty_string()
    {
        var body = """
        [ { "id": "ext-x", "name": "Orphan" } ]
        """;
        var list = TelavoxApiClient.ParseExtensions(body);
        Assert.Single(list);
        Assert.Equal(string.Empty, list[0].Number);
        Assert.Equal("Orphan", list[0].Name);
    }

    // ---- ParseCreateApiUserResponse ----

    [Fact]
    public void ParseCreateApiUserResponse_canonical_shape_lifts_three_fields()
    {
        var body = """
        {
          "email": "sd-capi-aaa@servicedesk.local",
          "userId": "tlvx-9001",
          "token": "ey.SHARP.SECRET"
        }
        """;
        var r = TelavoxApiClient.ParseCreateApiUserResponse(body);
        Assert.Equal("sd-capi-aaa@servicedesk.local", r.Email);
        Assert.Equal("tlvx-9001", r.UserId);
        Assert.Equal("ey.SHARP.SECRET", r.Token);
    }

    [Fact]
    public void ParseCreateApiUserResponse_accepts_id_instead_of_userId()
    {
        // Defensive: PAPI variants observed in docs use "id" instead of
        // "userId" for the api-user primary-key. Parser accepts both.
        var body = """
        { "email": "x@y.z", "id": "tlvx-1", "token": "ABC" }
        """;
        var r = TelavoxApiClient.ParseCreateApiUserResponse(body);
        Assert.Equal("tlvx-1", r.UserId);
        Assert.Equal("ABC", r.Token);
    }

    [Fact]
    public void ParseCreateApiUserResponse_accepts_apiToken_alias()
    {
        var body = """
        { "email": "x@y.z", "userId": "1", "apiToken": "PAYLOAD" }
        """;
        Assert.Equal("PAYLOAD", TelavoxApiClient.ParseCreateApiUserResponse(body).Token);
    }

    [Fact]
    public void ParseCreateApiUserResponse_empty_body_returns_blank_record()
    {
        // The TelavoxApiClient layer asserts a non-empty token before
        // returning, so an empty body here surfaces in the calling site
        // as a TelavoxApiException rather than silently succeeding.
        var r = TelavoxApiClient.ParseCreateApiUserResponse(string.Empty);
        Assert.Equal(string.Empty, r.Email);
        Assert.Equal(string.Empty, r.UserId);
        Assert.Equal(string.Empty, r.Token);
    }

    // ---- ParseCurrentCall ----

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
    public void ParseCurrentCall_bare_array_picks_first_call()
    {
        var body = """
        [
          {
            "id": "call-abc",
            "state": "answered",
            "from": "+32498123456",
            "to": "+3290011111",
            "startTime": "2026-05-11T09:00:00+00:00"
          }
        ]
        """;
        var call = TelavoxApiClient.ParseCurrentCall(body);
        Assert.NotNull(call);
        Assert.Equal("call-abc", call!.CallId);
        // States are uppercased so the worker can compare without
        // case-folding on every tick.
        Assert.Equal("ANSWERED", call.State);
        Assert.Equal("+32498123456", call.FromNumber);
        Assert.Equal("+3290011111", call.ToNumber);
        Assert.NotNull(call.StartUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 5, 11, 9, 0, 0, TimeSpan.Zero),
            call.StartUtc!.Value);
    }

    [Fact]
    public void ParseCurrentCall_envelope_under_calls_works()
    {
        var body = """
        {
          "calls": [
            { "callId": "c1", "state": "RINGING", "from": "+32498000001" }
          ]
        }
        """;
        var call = TelavoxApiClient.ParseCurrentCall(body);
        Assert.NotNull(call);
        Assert.Equal("c1", call!.CallId);
        Assert.Equal("RINGING", call.State);
        Assert.Null(call.ToNumber);
    }

    [Fact]
    public void ParseCurrentCall_falls_back_to_fromNumber_and_callee_aliases()
    {
        var body = """
        [
          { "id": "c-2", "state": "ANSWERED", "fromNumber": "+32498222222", "callee": "+3290011111" }
        ]
        """;
        var call = TelavoxApiClient.ParseCurrentCall(body);
        Assert.NotNull(call);
        Assert.Equal("+32498222222", call!.FromNumber);
        Assert.Equal("+3290011111", call.ToNumber);
    }

    [Fact]
    public void ParseCurrentCall_row_without_id_or_callId_is_skipped()
    {
        var body = """
        [ { "state": "ringing", "from": "x" } ]
        """;
        Assert.Null(TelavoxApiClient.ParseCurrentCall(body));
    }
}
