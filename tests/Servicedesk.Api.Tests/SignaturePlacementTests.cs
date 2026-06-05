using Servicedesk.Infrastructure.Signatures;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.61 — locks the compose→send signature placement contract: the bare
/// editor marker is detected, swapped for the rendered signature ABOVE the
/// quoted history, stripped when no signature applies, and never treated as a
/// regex substitution template.
public sealed class SignaturePlacementTests
{
    private const string Marker = "<div data-sd-signature=\"1\"></div>";

    [Fact]
    public void HasMarker_detects_only_a_real_marker()
    {
        Assert.True(SignaturePlacement.HasMarker($"<p>Hi</p>{Marker}<blockquote>q</blockquote>"));
        Assert.False(SignaturePlacement.HasMarker("<p>Hi</p>"));
        Assert.False(SignaturePlacement.HasMarker(null));
    }

    [Fact]
    public void ReplaceMarker_swaps_signature_in_above_the_quote()
    {
        var body = $"<p>My message</p>{Marker}<p>On … wrote:</p><blockquote>old</blockquote>";
        var result = SignaturePlacement.ReplaceMarker(body, "<div class=\"sd-signature\">SIG</div>");

        Assert.Contains("<div class=\"sd-signature\">SIG</div>", result);
        Assert.DoesNotContain("data-sd-signature", result);
        // The signature must land between the message and the quoted history.
        Assert.True(result.IndexOf("My message") < result.IndexOf("SIG"));
        Assert.True(result.IndexOf("SIG") < result.IndexOf("blockquote"));
    }

    [Fact]
    public void ReplaceMarker_treats_signature_as_literal_not_a_regex_template()
    {
        // A '$1' in the signature HTML must survive verbatim, not be read as a
        // capture-group reference.
        var result = SignaturePlacement.ReplaceMarker(Marker, "price is $1 each");
        Assert.Equal("price is $1 each", result);
    }

    [Fact]
    public void ReplaceMarker_keeps_only_one_signature_when_duplicated()
    {
        var body = $"{Marker}middle{Marker}";
        var result = SignaturePlacement.ReplaceMarker(body, "SIG");
        Assert.Equal("SIGmiddle", result);
    }

    [Fact]
    public void StripMarker_removes_a_bare_marker()
    {
        Assert.Equal("<p>Hi</p>", SignaturePlacement.StripMarker($"<p>Hi</p>{Marker}"));
    }

    [Fact]
    public void Marker_with_extra_attributes_and_whitespace_still_matches()
    {
        var m = "<div class=\"x\" data-sd-signature=\"1\" data-foo=\"y\">  </div>";
        Assert.Equal("", SignaturePlacement.StripMarker(m));
    }
}
