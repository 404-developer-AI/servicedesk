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
/// agent may already open.
public sealed record KbChatCitation(Guid ArticleId, string Title, string Slug, Guid SectionId);

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
