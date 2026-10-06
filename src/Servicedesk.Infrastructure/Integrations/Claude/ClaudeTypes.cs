using System.Text.Json.Nodes;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// One image to send inline with a proposal call. Base64 is used (rather than
/// the Files API) because it is the safest shape under zero data retention —
/// nothing is uploaded or persisted on Anthropic's side.
public sealed record ClaudeImageInput(string MediaType, string Base64Data);

/// Low-level result of a single Messages API call. Token counts drive cost;
/// <see cref="RequestId"/> is the Anthropic request-id kept for support
/// tracing; <see cref="StopReason"/> is inspected for refusals.
public sealed record ClaudeApiResult(
    string Text,
    string Model,
    int InputTokens,
    int OutputTokens,
    string? StopReason,
    string? RequestId);

/// Why a proposal request resolved the way it did. Anything other than
/// <see cref="Ok"/>/<see cref="Refused"/> is a guard that ran before any API
/// call (no cost incurred).
public enum ClaudeProposalOutcome
{
    Ok,
    Refused,
    Disabled,
    NotConfigured,
    ZdrNotConfirmed,
    NoBudget,
    BudgetExceeded,
}

/// Result of <see cref="IClaudeAssistService.GenerateProposalAsync"/>. On
/// <see cref="ClaudeProposalOutcome.Ok"/> the proposal fields and usage are
/// populated; on a guard outcome they are null and <see cref="Message"/>
/// explains why; on <see cref="ClaudeProposalOutcome.Refused"/> the assistant
/// declined and <see cref="Message"/> carries its explanation.
public sealed record ClaudeProposalResult(
    ClaudeProposalOutcome Outcome,
    string? ProposalText,
    string? ProposalHtml,
    string? Message,
    int InputTokens,
    int OutputTokens,
    long CostMicroEur,
    int ImageCount,
    long MonthSpendMicroEur,
    long MonthBudgetMicroEur);

/// Which Claude feature a usage row belongs to. Stored verbatim in
/// <c>claude_usage_log.feature</c> so the admin overview can split spend per
/// feature; all three share the one per-agent monthly budget.
public static class ClaudeFeatures
{
    /// In-ticket "Analyze &amp; propose a solution by AI".
    public const string Proposal = "proposal";

    /// In-ticket "Summary" — fills the admin-editable summary template.
    public const string Summary = "summary";

    /// Floating knowledge-base chat assistant.
    public const string KbChat = "kbchat";
}

/// Per-agent monthly usage row for the admin overview. Totals plus a split
/// per <see cref="ClaudeFeatures"/> value (calls exclude blocked attempts).
public sealed class ClaudeAgentUsage
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public int? BudgetOverrideCents { get; set; }
    public long MonthSpendMicroEur { get; set; }
    public int CallCount { get; set; }
    public long ProposalSpendMicroEur { get; set; }
    public int ProposalCalls { get; set; }
    public long SummarySpendMicroEur { get; set; }
    public int SummaryCalls { get; set; }
    public long KbChatSpendMicroEur { get; set; }
    public int KbChatCalls { get; set; }
}

// ---- KB chat assistant (tool-use, multi-turn) ----------------------------

/// One tool-use request the model made in a chat turn: the block <see cref="Id"/>
/// (echoed back in the matching tool_result), the tool <see cref="Name"/> and
/// the model-supplied <see cref="Input"/> arguments. The only tool the chat
/// assistant is ever given is the auth-scoped knowledge-base search.
public sealed record ClaudeToolUse(string Id, string Name, JsonObject Input);

/// Result of one Messages API round-trip in the KB chat tool-use loop.
/// <see cref="AssistantContent"/> is the raw response content array, echoed
/// back verbatim as the assistant turn on the next round-trip so the model
/// sees its own tool calls. <see cref="Text"/> is the concatenated text blocks;
/// <see cref="ToolUses"/> is non-empty when <see cref="StopReason"/> is
/// "tool_use". Token counts drive cost and budgeting.
public sealed record ClaudeChatResult(
    JsonNode AssistantContent,
    string Text,
    IReadOnlyList<ClaudeToolUse> ToolUses,
    string? StopReason,
    int InputTokens,
    int OutputTokens,
    string Model,
    string? RequestId);

/// One row to append to <c>claude_usage_log</c>. <see cref="Feature"/> is one
/// of the <see cref="ClaudeFeatures"/> constants.
public sealed record ClaudeUsageEntry(
    Guid? UserId,
    Guid? TicketId,
    string Model,
    int InputTokens,
    int OutputTokens,
    long CostMicroEur,
    int ImageCount,
    string Outcome,
    string? ErrorCode,
    string? RequestId,
    string Feature);
