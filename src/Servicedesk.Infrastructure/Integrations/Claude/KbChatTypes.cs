namespace Servicedesk.Infrastructure.Integrations.Claude;

/// One message in a KB-chat transcript exchanged with the frontend. Only the
/// visible text is carried — tool calls and tool results are reconstructed
/// server-side each turn and never trusted from the client.
public sealed record KbChatMessage(string Role, string Text);

/// Why a KB-chat turn resolved the way it did. Anything other than
/// <see cref="Ok"/> is a guard that ran before (or instead of) a billable API
/// call, except <see cref="Error"/> which is an upstream failure.
public enum KbChatOutcome
{
    Ok,
    Disabled,
    NotConfigured,
    ZdrNotConfirmed,
    NoKbAccess,
    NoBudget,
    BudgetExceeded,
}

/// A knowledge-base article the assistant was shown while answering, surfaced
/// to the agent as a clickable citation. The frontend links to
/// <c>/kb/articles/{ArticleId}</c>; the assistant only ever sees articles the
/// agent may already open. <see cref="Referenced"/> = the reply names this
/// article (the suggested answer); the others are related search hits.
public sealed record KbChatCitation(Guid ArticleId, string Title, string Slug, Guid SectionId, bool Referenced = false);

/// Orders the citations the way the agent reads them: articles the reply
/// names first (in the order the reply mentions them, flagged
/// <see cref="KbChatCitation.Referenced"/>), then every other search hit in
/// retrieval order. The prompt makes the model cite article titles, so a
/// title found in the reply text — compared case-, quote-, markdown- and
/// whitespace-insensitively — is a reference.
public static class KbChatCitationRanker
{
    public static IReadOnlyList<KbChatCitation> Rank(string? replyText, IReadOnlyList<KbChatCitation> citations)
    {
        var reply = Normalize(replyText);
        var referenced = new List<(int Position, KbChatCitation Citation)>();
        var related = new List<KbChatCitation>();
        foreach (var c in citations)
        {
            var title = Normalize(c.Title);
            var pos = title.Length >= 4 ? reply.IndexOf(title, StringComparison.Ordinal) : -1;
            if (pos >= 0) referenced.Add((pos, c with { Referenced = true }));
            else related.Add(c with { Referenced = false });
        }
        return referenced.OrderBy(r => r.Position).Select(r => r.Citation).Concat(related).ToList();
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            // Quotes and markdown emphasis the model wraps titles in.
            var skip = ch is '"' or '\'' or '`' or '*' or '_' or '“' or '”' or '‘' or '’' or '„' or '«' or '»';
            if (skip) continue;
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// Result of one KB-chat turn. On <see cref="KbChatOutcome.Ok"/> the reply and
/// citations are populated; on a guard outcome they are null/empty and
/// <see cref="Message"/> explains why. Usage/budget figures mirror the
/// ticket-assist result so the same UI can show remaining budget.
public sealed record KbChatResult(
    KbChatOutcome Outcome,
    string? ReplyText,
    string? ReplyHtml,
    string? Message,
    IReadOnlyList<KbChatCitation> Citations,
    int InputTokens,
    int OutputTokens,
    long CostMicroEur,
    long MonthSpendMicroEur,
    long MonthBudgetMicroEur);
