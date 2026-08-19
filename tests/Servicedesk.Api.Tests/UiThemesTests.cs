using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.108 — pins the shared theme-identifier contract used by the
/// per-user preference endpoints, /auth/me and the public default-theme
/// endpoint: three identifiers, case/whitespace-tolerant reads, unknown
/// values rejected (write) or floored to the factory default (read), and
/// the factory default itself is Steaan.
public sealed class UiThemesTests
{
    [Theory]
    [InlineData("steaan", "steaan")]
    [InlineData("  Steaan ", "steaan")]
    [InlineData("LIGHT", "light")]
    [InlineData("dark", "dark")]
    public void Normalize_accepts_known_themes_case_insensitively(string raw, string expected)
        => Assert.Equal(expected, UiThemes.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("system")]
    [InlineData("nebula")]
    [InlineData("steaan-dark")]
    public void Normalize_rejects_unknown_values(string? raw)
        => Assert.Null(UiThemes.Normalize(raw));

    [Fact]
    public void Factory_default_is_steaan()
    {
        Assert.Equal("steaan", UiThemes.Factory);
        Assert.Equal("steaan", UiThemes.NormalizeOrFactory(null));
        Assert.Equal("steaan", UiThemes.NormalizeOrFactory("garbage"));
        Assert.Equal("dark", UiThemes.NormalizeOrFactory("dark"));
    }

    [Fact]
    public void Setting_default_matches_factory()
    {
        var def = Assert.Single(SettingDefaults.All, d => d.Key == SettingKeys.Ui.DefaultTheme);
        Assert.Equal(UiThemes.Factory, def.Value);
    }
}
