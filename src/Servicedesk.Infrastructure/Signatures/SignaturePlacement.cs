using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Signatures;

/// Bridges the compose-window signature block and the send-time render. The
/// editor serialises the fixed signature block to a bare
/// <c>&lt;div data-sd-signature&gt;&lt;/div&gt;</c> marker at the agent's chosen
/// spot (directly under their message, above the quoted history). On send we
/// swap that marker for the authoritative cid-rendered signature, or strip it
/// when no signature applies — so a bare marker never reaches the recipient.
public static class SignaturePlacement
{
    public const string MarkerAttribute = "data-sd-signature";

    // The block is an atom node, so it always serialises empty: only optional
    // attributes and inner whitespace sit between the open/close tags.
    private static readonly Regex MarkerRegex = new(
        @"<div\b[^>]*\bdata-sd-signature\b[^>]*>\s*</div>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool HasMarker(string? body) =>
        !string.IsNullOrEmpty(body)
        && body.Contains(MarkerAttribute, StringComparison.OrdinalIgnoreCase);

    /// Replaces the (first) signature marker with the rendered signature HTML.
    /// Uses a match-evaluator so a <c>$</c> in the signature can't be read as a
    /// regex substitution. Any stray additional markers are stripped.
    public static string ReplaceMarker(string body, string signatureHtml)
    {
        if (string.IsNullOrEmpty(body)) return body;
        var replaced = false;
        return MarkerRegex.Replace(body, _ =>
        {
            if (replaced) return string.Empty;
            replaced = true;
            return signatureHtml ?? string.Empty;
        });
    }

    public static string StripMarker(string body) =>
        string.IsNullOrEmpty(body) ? body : MarkerRegex.Replace(body, string.Empty);
}
