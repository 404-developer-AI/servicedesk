using System.Globalization;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// One row of <c>Claude.ModelPrices</c>: a model id (or id prefix) and its
/// input/output price in euro cents per 1,000,000 tokens.
public sealed record ClaudeModelPrice(string Model, int InputCentsPerMTok, int OutputCentsPerMTok);

/// The per-model price list. Every Claude call is costed with the price of
/// the model that actually answered, so switching a feature to another model
/// can never leave it on the old model's price (the KB chat was costed at
/// Sonnet prices for months that way). A model missing from the list falls
/// back to the feature's own price settings.
///
/// Format: one model per line (or ';'-separated), <c>model = input / output</c>,
/// e.g. <c>claude-sonnet-5-5 = 200 / 1000</c>. '#' starts a comment. Matching
/// is case-insensitive on the longest prefix, so <c>claude-haiku-4-5</c> also
/// prices the dated <c>claude-haiku-4-5-20251001</c>.
public static class ClaudeModelPrices
{
    public const int MaxEntries = 50;

    public static IReadOnlyList<ClaudeModelPrice> Parse(string? raw)
    {
        var result = new List<ClaudeModelPrice>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var rawLine in raw.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var model = line[..eq].Trim();
            var prices = line[(eq + 1)..].Split('/', StringSplitOptions.TrimEntries);
            if (model.Length == 0 || prices.Length != 2) continue;
            if (!int.TryParse(prices[0], NumberStyles.None, CultureInfo.InvariantCulture, out var input)) continue;
            if (!int.TryParse(prices[1], NumberStyles.None, CultureInfo.InvariantCulture, out var output)) continue;
            result.Add(new ClaudeModelPrice(model, input, output));
            if (result.Count >= MaxEntries) break;
        }
        return result;
    }

    /// The price for <paramref name="model"/>: longest matching prefix, or
    /// null when the list has no entry for it.
    public static ClaudeModelPrice? Resolve(string? raw, string? model)
    {
        var m = (model ?? string.Empty).Trim();
        if (m.Length == 0) return null;
        return Parse(raw)
            .Where(p => m.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Model.Length)
            .FirstOrDefault();
    }
}
