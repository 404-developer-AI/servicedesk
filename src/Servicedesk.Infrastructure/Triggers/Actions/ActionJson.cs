using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Tiny convenience layer for parsing a single action's JSON. The action
/// schema is hand-edited via the admin UI (Block 6) so every reader needs
/// the same defensive shape: missing key returns false, wrong type returns
/// false, valid value populates the out-param. Keeps handlers free of
/// repetitive ValueKind / TryGetGuid plumbing.
internal static class ActionJson
{
    public static bool TryReadString(JsonElement el, string name, out string value)
    {
        value = string.Empty;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        var s = prop.GetString();
        if (string.IsNullOrEmpty(s)) return false;
        value = s;
        return true;
    }

    public static bool TryReadGuid(JsonElement el, string name, out Guid value)
    {
        value = default;
        if (!TryReadString(el, name, out var s)) return false;
        return Guid.TryParse(s, out value);
    }

    /// "Present-but-nullable" variant: returns true when the key exists
    /// and is either an explicit JSON null or a valid Guid string. Used by
    /// handlers whose target column is nullable (e.g. set_owner where
    /// `user_id: null` clears the assignee). A missing key or wrong type
    /// still returns false so the handler can distinguish a malformed
    /// action from an explicit clear.
    public static bool TryReadGuidOrNull(JsonElement el, string name, out Guid? value)
    {
        value = null;
        if (!el.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Null) return true;
        if (prop.ValueKind != JsonValueKind.String) return false;
        var s = prop.GetString();
        if (string.IsNullOrEmpty(s)) return true;
        if (!Guid.TryParse(s, out var g)) return false;
        value = g;
        return true;
    }

    public static bool TryReadBool(JsonElement el, string name, out bool value)
    {
        value = default;
        if (!el.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }
}
