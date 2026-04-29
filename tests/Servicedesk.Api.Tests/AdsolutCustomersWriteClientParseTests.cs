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
}
