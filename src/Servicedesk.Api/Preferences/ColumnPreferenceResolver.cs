using System.Text.RegularExpressions;
using static Servicedesk.Api.Preferences.UserPreferencesEndpoints;

namespace Servicedesk.Api.Preferences;

/// v0.1.18 — the ticket-list column cascade as a pure function so the
/// per-view lock is unit-testable without a database.
public static partial class ColumnPreferenceResolver
{
    /// Hard cap on a stored layout; the full column registry is far below it.
    public const int MaxLayoutLength = 1000;

    [GeneratedRegex("^[A-Za-z]+(,[A-Za-z]+)*$")]
    private static partial Regex LayoutPattern();

    public static ColumnPreference Resolve(
        string? userPerView, string? viewColumns, bool viewAllowsUserColumns,
        string? userGeneral, string adminDefault)
    {
        if (!viewAllowsUserColumns)
        {
            return viewColumns is not null
                ? new ColumnPreference(viewColumns, "view", Locked: true)
                : new ColumnPreference(adminDefault, "default", Locked: true);
        }
        if (userPerView is not null) return new ColumnPreference(userPerView, "user-view");
        if (viewColumns is not null) return new ColumnPreference(viewColumns, "view");
        if (userGeneral is not null) return new ColumnPreference(userGeneral, "user");
        return new ColumnPreference(adminDefault, "default");
    }

    /// Column ids are plain camelCase identifiers; anything else is rejected
    /// before it is stored. Empty is allowed (= "no columns picked").
    public static bool IsValidLayout(string? columns) =>
        columns is not null
        && columns.Length <= MaxLayoutLength
        && (columns.Length == 0 || LayoutPattern().IsMatch(columns));
}
