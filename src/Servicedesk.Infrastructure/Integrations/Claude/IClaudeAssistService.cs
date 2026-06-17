namespace Servicedesk.Infrastructure.Integrations.Claude;

/// Orchestrates the in-ticket "AI proposal" feature: enforces the kill-switch,
/// the zero-data-retention confirmation and the per-agent monthly budget;
/// assembles the strictly-scoped prompt from the ticket; optionally attaches
/// agent-selected screenshots; calls the API; and logs usage/cost. Queue
/// access for the ticket is enforced by the caller (the endpoint) before this
/// runs.
public interface IClaudeAssistService
{
    Task<ClaudeProposalResult> GenerateProposalAsync(
        Guid ticketId,
        Guid userId,
        IReadOnlyList<Guid> selectedAttachmentIds,
        CancellationToken ct);
}
