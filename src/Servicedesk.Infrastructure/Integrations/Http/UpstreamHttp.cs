using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.Http;

/// Small helpers shared by the integration API clients when they turn a
/// failed upstream response into an audit-safe message.
public static class UpstreamHttp
{
    /// Reads the response body; an unreadable body is an empty string.
    public static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    /// Extracts an upstream error code from <c>{ error: "..." }</c> or
    /// <c>{ error: { code: "..." } }</c>. With <paramref name="includeTopLevelCode"/>
    /// a top-level <c>{ code: "..." }</c> is also accepted (Telavox envelopes).
    public static string? TryParseErrorCode(string body, bool includeTopLevelCode = false)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString();
                }
            }
            if (includeTopLevelCode &&
                doc.RootElement.TryGetProperty("code", out var topCode) &&
                topCode.ValueKind == JsonValueKind.String)
            {
                return topCode.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// Cuts to <paramref name="max"/> characters and appends an ellipsis;
    /// null or empty input is returned as is.
    public static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    /// Hard cut to <paramref name="max"/> characters, no ellipsis.
    public static string TruncateNoEllipsis(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
