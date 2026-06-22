namespace Servicedesk.Infrastructure.Integrations.Claude;

/// The knowledge-base chat assistant (v0.0.86). Answers an agent's question
/// using ONLY the knowledge base that agent may already see, via a single
/// auth-scoped search tool — no internet, no other capability. Shares the
/// Claude integration's API key, zero-data-retention gate, per-agent budget,
/// pricing and usage log; has its own kill-switch, model and prompt.
///
/// Authorization is enforced here, not by the model: the per-user KB-access
/// gate is checked once per turn, and every search is constrained to
/// Internal/Published articles — the model never receives an article the agent
/// could not open. A turn makes at most <c>KbChatMaxSearches + 2</c> Messages
/// API calls (the initial call, one per search, and a forced final answer
/// once the search budget is spent).
public interface IKbChatService
{
    Task<KbChatResult> SendAsync(
        Guid userId,
        IReadOnlyList<KbChatMessage> history,
        string userMessage,
        CancellationToken ct);
}
