using System.Globalization;
using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.Http;

/// Lenient property readers for upstream JSON. A missing property or one of
/// the wrong kind yields null / the stated default — never an exception.
/// The method names carry the accepted shapes on purpose: the integrations
/// differ (strict string vs number-as-string, blank handling, date styles).
public static class JsonElementExtensions
{
    /// The string value; any other kind (number, null, ...) is null.
    public static string? GetStringOrNull(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    /// Like <see cref="GetStringOrNull"/>, but a blank/whitespace string is null.
    public static string? GetNonBlankString(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return null;
        var s = prop.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// A string, or a number rendered as its raw text; anything else is null.
    public static string? GetStringOrNumber(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            _ => null,
        };
    }

    public static bool TryGetGuidProperty(this JsonElement el, string name, out Guid value)
    {
        value = Guid.Empty;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(prop.GetString(), out value);
    }

    public static Guid? GetGuidOrNull(this JsonElement el, string name) =>
        el.TryGetGuidProperty(name, out var g) ? g : null;

    /// A JSON number that fits an int; anything else is null.
    public static int? GetInt32OrNull(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v)) return v;
        return null;
    }

    /// A JSON number that fits an int, or a numeric string (current culture);
    /// anything else is null.
    public static int? GetInt32OrNumericString(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var v) => v,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var v) => v,
            _ => null,
        };
    }

    /// A JSON number or an invariant-culture numeric string, else <paramref name="fallback"/>.
    public static int GetInt32OrNumericString(this JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var prop)) return fallback;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)) return value;
        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    /// A JSON number that fits a long, or a numeric string (current culture); else null.
    public static long? GetInt64OrNumericString(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt64(out var v) => v,
            JsonValueKind.String when long.TryParse(prop.GetString(), out var v) => v,
            _ => null,
        };
    }

    public static decimal? GetDecimalOrNull(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var v)) return v;
        return null;
    }

    /// true/false; a missing property or any other kind yields <paramref name="defaultValue"/>.
    public static bool GetBool(this JsonElement el, string name, bool defaultValue = false)
    {
        if (!el.TryGetProperty(name, out var prop)) return defaultValue;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    /// true/false; a missing property or any other kind is null.
    public static bool? GetBoolOrNull(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// Adsolut serialises dates offset-less (no 'Z'), e.g. "2024-02-21T22:24:49".
    /// Parsed as UTC (AssumeUniversal). The sentinel "0001-01-01..." means
    /// "unset" and yields null, so a default date never lands in a mirror.
    public static DateTimeOffset? GetDateTimeOffsetUnsetAsNull(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return null;
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.StartsWith("0001-01-01", StringComparison.Ordinal)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUniversalTime()
            : null;
    }

    /// An ISO date string parsed as UTC when it carries no offset, normalised
    /// to UTC. No "unset" sentinel handling.
    public static DateTimeOffset? GetUtcDateTimeOffset(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return null;
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v)
            ? v
            : null;
    }
}
