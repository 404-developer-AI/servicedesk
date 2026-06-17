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

/// Per-agent monthly usage row for the admin overview.
public sealed record ClaudeAgentUsage(
    Guid UserId,
    string Email,
    string RoleName,
    int? BudgetOverrideCents,
    long MonthSpendMicroEur,
    int CallCount);

/// One row to append to <c>claude_usage_log</c>.
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
    string? RequestId);
