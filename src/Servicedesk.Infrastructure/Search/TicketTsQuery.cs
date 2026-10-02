using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Search;

/// Shared prefix-tsquery builder for ticket full-text search: global search
/// (<see cref="TicketSearchSource"/>) and the per-view "Full" search on the
/// ticket list (v0.1.18) must match the same way.
public static partial class TicketTsQuery
{
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenPattern();

    /// Each alphanumeric token gets a `:*` suffix so "bench" matches
    /// "benchmark"; tokens are AND-ed. Everything that is not a letter or
    /// digit acts as a separator, so the result is always valid
    /// `to_tsquery('simple', …)` syntax. Empty input → empty string.
    public static string BuildPrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var tokens = TokenPattern().Matches(raw.ToLowerInvariant()).Select(m => m.Value + ":*");
        return string.Join(" & ", tokens);
    }
}
