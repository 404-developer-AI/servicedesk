using Servicedesk.Infrastructure.Integrations.Adsolut;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.27 — pins the offset-detection rules of the strict-parse canary.
/// The push-loop preventie depends on Adsolut's lastModified being
/// timezone-anchored. Empirical verification against the production
/// dossier confirmed `+00:00` suffixes on every observed record; this
/// test guards the detector that fires a log warning if WK ever switches
/// to offset-less serialisation.
public sealed class AdsolutCustomersClientCanaryTests
{
    [Theory]
    [InlineData("2026-04-29T11:44:20Z")]
    [InlineData("2026-04-29T11:44:20+00:00")]
    [InlineData("2026-04-29T11:44:20-00:00")]
    [InlineData("2026-04-29T11:44:20+02:00")]
    [InlineData("2026-04-29T11:44:20-05:30")]
    [InlineData("2026-04-29T11:44:20+0200")]
    [InlineData("2026-04-29T11:44:20-0530")]
    public void Offset_suffixed_strings_are_not_flagged(string raw)
    {
        Assert.False(AdsolutCustomersClient.LooksOffsetless(raw));
    }

    [Theory]
    [InlineData("2026-04-29T11:44:20")]
    [InlineData("2026-04-29T11:44:20.123")]
    [InlineData("2026-04-29T11:44")]
    public void Offset_less_strings_are_flagged(string raw)
    {
        Assert.True(AdsolutCustomersClient.LooksOffsetless(raw));
    }

    [Fact]
    public void Empty_string_is_not_flagged()
    {
        Assert.False(AdsolutCustomersClient.LooksOffsetless(string.Empty));
    }
}
