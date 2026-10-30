using System.Text.Json.Nodes;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// Thin client over the Anthropic Messages API. The callers (the assist and
/// chat services) own scoping, budget enforcement and usage logging; this type
/// only reads the key + connection settings, makes the HTTP call, and parses
/// the result. Throws <see cref="ClaudeApiException"/> on transport or non-2xx.
public interface IClaudeApiClient
{
    /// Ticket-assist call (proposal or summary): a single, stateless,
    /// tool-free call. <paramref name="auditEventType"/> is the
    /// <c>integration_audit</c> event type the call is logged under
    /// (<see cref="ClaudeEventTypes.ProposalCall"/> /
    /// <see cref="ClaudeEventTypes.SummaryCall"/>). <paramref name="model"/>
    /// and <paramref name="effort"/> are resolved per feature by the caller.
    Task<ClaudeApiResult> CreateProposalAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<ClaudeImageInput> images,
        string auditEventType,
        string model,
        string effort,
        CancellationToken ct);

    /// One round-trip of the KB-chat tool-use loop. The caller appends the
    /// returned assistant content and any tool results to
    /// <see cref="ClaudeChatRequest.Messages"/>, then calls again. Model and
    /// max-tokens are passed explicitly (chat uses its own settings).
    Task<ClaudeChatResult> CreateChatTurnAsync(ClaudeChatRequest request, CancellationToken ct);
}

/// Input for <see cref="IClaudeApiClient.CreateChatTurnAsync"/>.
/// <see cref="SystemContext"/> is a second system block (the KB article
/// index) sent after the instructions. With <see cref="PromptCaching"/> the
/// tools + system blocks and the transcript up to the last message are marked
/// cacheable. <see cref="ForceTextAnswer"/> keeps the tools (so the cached
/// prefix stays identical) but sets <c>tool_choice: none</c>, forcing a text
/// answer once the search budget is spent. <see cref="Effort"/> (low |
/// medium | high) is sent as <c>output_config.effort</c> only on models that
/// accept it.
public sealed record ClaudeChatRequest(
    string SystemPrompt,
    string? SystemContext,
    JsonArray Messages,
    JsonArray Tools,
    string Model,
    int MaxTokens,
    bool PromptCaching,
    bool ForceTextAnswer,
    string? Effort = null);
