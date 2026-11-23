using System.Text.Json;
using Servicedesk.Infrastructure.Integrations.Http;
using Servicedesk.Infrastructure.Time;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class JsonElementExtensionsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void String_variants_differ_on_numbers_and_blanks()
    {
        var el = Parse("""{"s":"x","blank":"  ","n":12,"nul":null}""");

        Assert.Equal("x", el.GetStringOrNull("s"));
        Assert.Equal("  ", el.GetStringOrNull("blank"));
        Assert.Null(el.GetStringOrNull("n"));
        Assert.Null(el.GetStringOrNull("nul"));
        Assert.Null(el.GetStringOrNull("missing"));

        Assert.Null(el.GetNonBlankString("blank"));
        Assert.Equal("x", el.GetNonBlankString("s"));

        Assert.Equal("12", el.GetStringOrNumber("n"));
        Assert.Equal("x", el.GetStringOrNumber("s"));
        Assert.Null(el.GetStringOrNumber("nul"));
    }

    [Fact]
    public void Guid_reads_only_string_guids()
    {
        var id = Guid.NewGuid();
        var el = Parse($$"""{"id":"{{id}}","bad":"nope","n":1}""");

        Assert.True(el.TryGetGuidProperty("id", out var got));
        Assert.Equal(id, got);
        Assert.False(el.TryGetGuidProperty("bad", out var empty));
        Assert.Equal(Guid.Empty, empty);
        Assert.False(el.TryGetGuidProperty("n", out _));
        Assert.Equal(id, el.GetGuidOrNull("id"));
        Assert.Null(el.GetGuidOrNull("bad"));
        Assert.Null(el.GetGuidOrNull("missing"));
    }

    [Fact]
    public void Numbers_strict_versus_numeric_string()
    {
        var el = Parse("""{"i":7,"s":"8","f":1.5,"big":9999999999,"txt":"abc","d":12.25}""");

        Assert.Equal(7, el.GetInt32OrNull("i"));
        Assert.Null(el.GetInt32OrNull("s"));
        Assert.Null(el.GetInt32OrNull("f"));
        Assert.Null(el.GetInt32OrNull("big"));

        Assert.Equal(8, el.GetInt32OrNumericString("s"));
        Assert.Equal(7, el.GetInt32OrNumericString("i"));
        Assert.Null(el.GetInt32OrNumericString("txt"));

        Assert.Equal(8, el.GetInt32OrNumericString("s", -1));
        Assert.Equal(-1, el.GetInt32OrNumericString("txt", -1));
        Assert.Equal(-1, el.GetInt32OrNumericString("missing", -1));

        Assert.Equal(9999999999L, el.GetInt64OrNumericString("big"));
        Assert.Equal(8L, el.GetInt64OrNumericString("s"));
        Assert.Null(el.GetInt64OrNumericString("txt"));

        Assert.Equal(12.25m, el.GetDecimalOrNull("d"));
        Assert.Null(el.GetDecimalOrNull("s"));
    }

    [Fact]
    public void Bool_variants_differ_on_missing_and_foreign_kinds()
    {
        var el = Parse("""{"t":true,"f":false,"s":"true"}""");

        Assert.True(el.GetBool("t"));
        Assert.False(el.GetBool("f", defaultValue: true));
        Assert.False(el.GetBool("s"));
        Assert.True(el.GetBool("s", defaultValue: true));
        Assert.True(el.GetBool("missing", defaultValue: true));

        Assert.True(el.GetBoolOrNull("t"));
        Assert.False(el.GetBoolOrNull("f"));
        Assert.Null(el.GetBoolOrNull("s"));
        Assert.Null(el.GetBoolOrNull("missing"));
    }

    [Fact]
    public void Dates_unset_sentinel_only_nulled_by_the_adsolut_variant()
    {
        var el = Parse("""{"a":"2024-02-21T22:24:49","z":"2024-02-21T22:24:49+02:00","unset":"0001-01-01T00:00:00","bad":"x","n":5}""");

        Assert.Equal(new DateTimeOffset(2024, 2, 21, 22, 24, 49, TimeSpan.Zero), el.GetDateTimeOffsetUnsetAsNull("a"));
        Assert.Equal(new DateTimeOffset(2024, 2, 21, 20, 24, 49, TimeSpan.Zero), el.GetDateTimeOffsetUnsetAsNull("z"));
        Assert.Null(el.GetDateTimeOffsetUnsetAsNull("unset"));
        Assert.Null(el.GetDateTimeOffsetUnsetAsNull("bad"));
        Assert.Null(el.GetDateTimeOffsetUnsetAsNull("n"));

        Assert.Equal(new DateTimeOffset(2024, 2, 21, 22, 24, 49, TimeSpan.Zero), el.GetUtcDateTimeOffset("a"));
        Assert.Equal(new DateTimeOffset(2024, 2, 21, 20, 24, 49, TimeSpan.Zero), el.GetUtcDateTimeOffset("z"));
        Assert.NotNull(el.GetUtcDateTimeOffset("unset"));
        Assert.Null(el.GetUtcDateTimeOffset("bad"));
    }
}

public sealed class UpstreamHttpTests
{
    [Fact]
    public void Truncate_variants()
    {
        Assert.Equal("abc…", UpstreamHttp.Truncate("abcdef", 3));
        Assert.Equal("abc", UpstreamHttp.Truncate("abc", 3));
        Assert.Equal("", UpstreamHttp.Truncate("", 3));
        Assert.Equal("abc", UpstreamHttp.TruncateNoEllipsis("abcdef", 3));
        Assert.Equal("abc", UpstreamHttp.TruncateNoEllipsis("abc", 3));
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("not json", null, null)]
    [InlineData("[1]", null, null)]
    [InlineData("""{"error":"bad"}""", "bad", "bad")]
    [InlineData("""{"error":{"code":"nested"}}""", "nested", "nested")]
    [InlineData("""{"code":"top"}""", null, "top")]
    [InlineData("""{"error":{"msg":"x"},"code":"top"}""", null, "top")]
    public void TryParseErrorCode_top_level_code_is_opt_in(string body, string? strict, string? withTopLevel)
    {
        Assert.Equal(strict, UpstreamHttp.TryParseErrorCode(body));
        Assert.Equal(withTopLevel, UpstreamHttp.TryParseErrorCode(body, includeTopLevelCode: true));
    }

    [Fact]
    public async Task SafeReadBodyAsync_returns_body_and_swallows_read_failures()
    {
        using var ok = new HttpResponseMessage { Content = new StringContent("hello") };
        Assert.Equal("hello", await UpstreamHttp.SafeReadBodyAsync(ok, CancellationToken.None));

        using var broken = new HttpResponseMessage { Content = new ThrowingContent() };
        Assert.Equal(string.Empty, await UpstreamHttp.SafeReadBodyAsync(broken, CancellationToken.None));
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, global::System.Net.TransportContext? context) =>
            throw new IOException("boom");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

public sealed class TimeZoneResolverTests
{
    [Fact]
    public void Resolve_returns_the_zone_or_the_callers_fallback()
    {
        Assert.Equal("Europe/Brussels", TimeZoneResolver.Resolve("Europe/Brussels", TimeZoneInfo.Utc).Id);
        Assert.Same(TimeZoneInfo.Utc, TimeZoneResolver.Resolve(null, TimeZoneInfo.Utc));
        Assert.Same(TimeZoneInfo.Utc, TimeZoneResolver.Resolve("  ", TimeZoneInfo.Utc));
        Assert.Same(TimeZoneInfo.Utc, TimeZoneResolver.Resolve("Mars/Olympus", TimeZoneInfo.Utc));
        Assert.Same(TimeZoneInfo.Local, TimeZoneResolver.Resolve("Mars/Olympus", TimeZoneInfo.Local));
    }

    [Fact]
    public void Lookup_and_IsValid_report_unknown_ids()
    {
        Assert.Equal(TimeZoneLookup.Found, TimeZoneResolver.Lookup("Europe/Brussels", out var zone));
        Assert.NotNull(zone);
        Assert.Equal(TimeZoneLookup.NotFound, TimeZoneResolver.Lookup("Mars/Olympus", out var none));
        Assert.Null(none);
        Assert.Equal(TimeZoneLookup.NotFound, TimeZoneResolver.Lookup(" ", out _));

        Assert.True(TimeZoneResolver.IsValid("Europe/Brussels"));
        Assert.False(TimeZoneResolver.IsValid("Mars/Olympus"));
        Assert.False(TimeZoneResolver.IsValid(null));
    }
}
