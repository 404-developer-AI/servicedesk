namespace Servicedesk.Infrastructure.Triggers.StatusGate;

/// Server-side matcher + note-renderer for v0.0.42 status-change gates.
/// Used by the ticket PATCH endpoint to enforce the same gate-evaluation
/// the agent UI sees before applying a mutation, plus by the read-only
/// "list matching gates" endpoint the UI calls to decide whether to open
/// a dialog. Stateless — both methods accept a ticket id and re-read the
/// snapshot so concurrent edits cannot stale the result.
public interface IStatusGateService
{
    /// Returns the gates that match a proposed status change. Order is
    /// stable (by trigger name) so the UI walks them deterministically.
    /// Empty list means the agent can apply the change without
    /// confirmation.
    Task<IReadOnlyList<MatchedStatusGate>> FindMatchingAsync(
        Guid ticketId, Guid targetStatusId, CancellationToken ct);

    /// Renders the gate's note_template against the current ticket
    /// snapshot, substituting <c>#{prompt.&lt;key&gt;}</c> for every
    /// supplied answer and <c>#{agent.name}</c> / <c>#{agent.email}</c>
    /// with the confirming user. Returns null when the template renders
    /// to whitespace — callers skip note insertion to keep the timeline
    /// clean.
    Task<string?> RenderNoteAsync(
        MatchedStatusGate gate,
        Guid ticketId,
        Guid agentUserId,
        IReadOnlyDictionary<string, string>? answers,
        CancellationToken ct);
}
