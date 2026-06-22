using System.Text.Json.Nodes;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// Thin client over the Anthropic Messages API. The callers (the assist and
/// chat services) own scoping, budget enforcement and usage logging; this type
/// only reads the key + connection settings, makes the HTTP call, and parses
/// the result. Throws <see cref="ClaudeApiException"/> on transport or non-2xx.
public interface IClaudeApiClient
{
    /// Ticket-assist proposal: a single, stateless, tool-free call.
    Task<ClaudeApiResult> CreateProposalAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<ClaudeImageInput> images,
        CancellationToken ct);

    /// One round-trip of the KB-chat tool-use loop. <paramref name="messages"/>
    /// is the running transcript (user/assistant content-block arrays) and
    /// <paramref name="tools"/> the tool definitions; the caller appends the
    /// returned assistant content and any tool results, then calls again.
    /// Model and max-tokens are passed explicitly (chat uses its own settings).
    Task<ClaudeChatResult> CreateChatTurnAsync(
        string systemPrompt,
        JsonArray messages,
        JsonArray tools,
        string model,
        int maxTokens,
        CancellationToken ct);
}
