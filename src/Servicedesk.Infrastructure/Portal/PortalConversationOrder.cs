namespace Servicedesk.Infrastructure.Portal;

/// v0.1.15 — the order of the conversation on a portal ticket. The customer's
/// own choice lives in <c>user_preferences</c> under <see cref="PreferenceKey"/>;
/// without one the admin default (<c>Portal.ConversationOrder</c>) applies.
/// Display only — the agent timeline is not affected.
public static class PortalConversationOrder
{
    public const string Oldest = "oldest";
    public const string Newest = "newest";
    public const string PreferenceKey = "portal:conversation-order";

    /// Canonical value, or null for anything that is not a known order.
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (string.Equals(v, Oldest, StringComparison.OrdinalIgnoreCase)) return Oldest;
        if (string.Equals(v, Newest, StringComparison.OrdinalIgnoreCase)) return Newest;
        return null;
    }

    /// The customer's choice when valid, else the admin default, else oldest
    /// first (a hand-edited row never breaks the page).
    public static string Resolve(string? userChoice, string? adminDefault) =>
        Normalize(userChoice) ?? Normalize(adminDefault) ?? Oldest;
}
