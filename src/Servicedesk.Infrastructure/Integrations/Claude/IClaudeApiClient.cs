namespace Servicedesk.Infrastructure.Integrations.Claude;

/// Thin client over the Anthropic Messages API for the ticket-assist feature.
/// One method: a single, stateless, tool-free call. The caller (the assist
/// service) owns scoping, budget enforcement and usage logging; this type only
/// reads the key + connection settings, makes the HTTP call, and parses the
/// result. Throws <see cref="ClaudeApiException"/> on transport or non-2xx.
public interface IClaudeApiClient
{
    Task<ClaudeApiResult> CreateProposalAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<ClaudeImageInput> images,
        CancellationToken ct);
}
