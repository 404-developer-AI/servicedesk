using System.Text.RegularExpressions;
using Servicedesk.Infrastructure.KnowledgeBase;

namespace Servicedesk.Infrastructure.Workflow;

/// A template candidate for detection: its id and sanitised body HTML.
public sealed record WorkflowTemplateCandidate(Guid Id, string BodyHtml);

public sealed record WorkflowTemplateMatch(Guid TemplateId, bool Filled);

/// v0.1.32 — recognises which compose template a note/call was built from,
/// from its content alone, and whether the agent filled it in.
///
/// Why content and not a client-sent id: it covers the <c>::</c> picker,
/// auto-insert, reloaded drafts and copy-paste alike, and a client cannot
/// claim a template it did not use.
///
/// <list type="bullet">
/// <item><b>Used</b>: at least <see cref="MinCoverage"/> of the template's
/// static words (its text with every <c>{{token}}</c> removed, counted as
/// a multiset) appear in the body. Templates with fewer than
/// <see cref="MinStaticWords"/> static words are never matched — too little
/// text to tell them from ordinary writing. Best coverage wins, ties go to
/// the template with more static words.</item>
/// <item><b>Filled</b>: the body holds at least one word more than the
/// template as it was inserted for this ticket (tokens resolved exactly
/// like the client's <c>substituteComposeTokens</c>: a non-empty value
/// replaces the token, an empty one leaves the raw <c>{{…}}</c>). An
/// untouched template — headings with empty bullets — is not filled.</item>
/// </list>
/// Both thresholds are detection guards, not tunables.
public static class WorkflowTemplateDetector
{
    public const double MinCoverage = 0.9;
    public const int MinStaticWords = 3;

    private static readonly Regex TokenPattern = new(@"\{\{\s*([\w.]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static WorkflowTemplateMatch? Detect(
        string? bodyHtml,
        IReadOnlyList<WorkflowTemplateCandidate> candidates,
        IReadOnlyDictionary<string, string>? tokens)
    {
        if (string.IsNullOrWhiteSpace(bodyHtml) || candidates.Count == 0) return null;
        var body = Bag(KbBodyStripper.HtmlToText(bodyHtml));
        if (body.Count == 0) return null;

        WorkflowTemplateCandidate? best = null;
        double bestCoverage = 0;
        int bestSize = 0;
        foreach (var c in candidates)
        {
            var staticWords = Bag(KbBodyStripper.HtmlToText(TokenPattern.Replace(c.BodyHtml, " ")));
            var size = staticWords.Values.Sum();
            if (size < MinStaticWords) continue;
            var covered = staticWords.Sum(kv => Math.Min(kv.Value, body.GetValueOrDefault(kv.Key)));
            var coverage = (double)covered / size;
            if (coverage < MinCoverage) continue;
            if (coverage > bestCoverage || (coverage == bestCoverage && size > bestSize))
            {
                best = c;
                bestCoverage = coverage;
                bestSize = size;
            }
        }
        if (best is null) return null;

        var rendered = Bag(KbBodyStripper.HtmlToText(Substitute(best.BodyHtml, tokens)));
        var filled = body.Any(kv => kv.Value > rendered.GetValueOrDefault(kv.Key));
        return new WorkflowTemplateMatch(best.Id, filled);
    }

    /// Mirrors <c>substituteComposeTokens</c> (lib/composeTokens.ts).
    internal static string Substitute(string html, IReadOnlyDictionary<string, string>? tokens)
        => TokenPattern.Replace(html, m =>
        {
            var canonical = "{{" + m.Groups[1].Value + "}}";
            return tokens is not null && tokens.TryGetValue(canonical, out var v) && !string.IsNullOrEmpty(v)
                ? System.Net.WebUtility.HtmlEncode(v)
                : m.Value;
        });

    private static Dictionary<string, int> Bag(string? text)
    {
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return bag;
        foreach (Match m in WordPattern.Matches(text.ToLowerInvariant()))
            bag[m.Value] = bag.GetValueOrDefault(m.Value) + 1;
        return bag;
    }
}
