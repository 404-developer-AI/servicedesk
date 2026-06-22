namespace Servicedesk.Infrastructure.Integrations.Claude;

/// Event-type constants for the Claude AI assist integration. Admin actions
/// (key/budget changes) go to the tamper-evident <c>audit_log</c>; the
/// per-call request/outcome rows go to <c>integration_audit</c> alongside the
/// other connectors so an admin can see upstream failures in the same place.
public static class ClaudeEventTypes
{
    /// Integration name written to the <c>integration_audit.integration</c>
    /// column and used as the SignalR status-key.
    public const string Integration = "claude-ai";

    // audit_log (security trail for admin actions)
    public const string ApiKeyUpdated = "integration.claude.api_key.updated";
    public const string ApiKeyDeleted = "integration.claude.api_key.deleted";
    public const string AgentBudgetUpdated = "integration.claude.agent_budget.updated";
    public const string ProposalRequested = "integration.claude.proposal.requested";

    /// An agent sent a message to the KB chat assistant (audit trail of who
    /// asked, and the outcome — not the content).
    public const string KbChatRequested = "integration.claude.kbchat.requested";

    // integration_audit (operational log for Messages API calls)
    public const string ProposalCall = "proposal.call";

    /// One Messages API round-trip made inside a KB chat turn (a turn may make
    /// several when the model searches).
    public const string ChatCall = "chat.call";
}
