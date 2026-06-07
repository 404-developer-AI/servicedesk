namespace Servicedesk.Infrastructure.Triggers.FirstOpenGate;

/// Server-side matcher + confirmation runner for first-open title-review
/// gates. Used by the ticket detail page's open-gate probe (read-only)
/// and by the confirmation endpoint. Stateless — each call re-reads the
/// ticket snapshot so concurrent edits cannot stale the result.
public interface IFirstOpenGateService
{
    /// Returns the first active <c>gate:first_open</c> trigger whose
    /// conditions match the ticket, or null when none match or the
    /// ticket's title has already been reviewed. "First" follows the
    /// repository's alphabetical-by-name order so the probe is
    /// deterministic. Fails closed: a malformed row is logged and skipped.
    Task<MatchedFirstOpenGate?> FindMatchingAsync(Guid ticketId, CancellationToken ct);

    /// Runs the gate trigger's actions after the agent has confirmed the
    /// title review. The caller is responsible for having already applied
    /// the (possibly edited) subject and marked the ticket reviewed; this
    /// method only dispatches the trigger's remaining actions (e.g.
    /// add_internal_note) with a render context that exposes
    /// <c>#{agent.name}</c> / <c>#{agent.email}</c> for the confirming
    /// agent and <c>#{ticket.subject_previous}</c> for the pre-edit title
    /// (the live <c>#{ticket.subject}</c> already reflects the new value).
    /// Best-effort: action failures are recorded in trigger_runs but never
    /// thrown, so a flaky note action can't roll back the confirmed review.
    Task RunConfirmActionsAsync(
        Guid triggerId,
        Guid ticketId,
        Guid agentUserId,
        string previousSubject,
        CancellationToken ct);
}
