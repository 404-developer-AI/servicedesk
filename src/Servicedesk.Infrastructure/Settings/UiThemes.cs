namespace Servicedesk.Infrastructure.Settings;

/// <summary>
/// The UI theme identifiers shared by the per-user preference
/// (<c>user_preferences.ui:theme</c>), the instance default
/// (<c>Ui.DefaultTheme</c>) and the public bootstrap endpoint.
///
/// <list type="bullet">
/// <item><c>steaan</c> — the flat, light-only Steaan house style
///   (v0.0.108). Factory default for new installs and for users who never
///   made an explicit choice.</item>
/// <item><c>light</c> / <c>dark</c> — the two modes of the original
///   "Nebula" glassmorphism theme (v0.0.44). The identifiers are kept as-is
///   so existing preference rows stay valid without a migration.</item>
/// </list>
/// </summary>
public static class UiThemes
{
    public const string Steaan = "steaan";
    public const string Light = "light";
    public const string Dark = "dark";

    /// The factory floor — applied when neither the user nor the admin
    /// default yields a valid value.
    public const string Factory = Steaan;

    public static readonly IReadOnlyList<string> All = new[] { Steaan, Light, Dark };

    /// Returns the canonical lowercase identifier when <paramref name="raw"/>
    /// is one of the supported themes, otherwise <c>null</c>. Used both at
    /// write time (reject bad input) and at read time (silently coerce a
    /// hand-edited DB row).
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        return v is Steaan or Light or Dark ? v : null;
    }

    /// <see cref="Normalize"/> with the factory fallback applied.
    public static string NormalizeOrFactory(string? raw) => Normalize(raw) ?? Factory;
}
