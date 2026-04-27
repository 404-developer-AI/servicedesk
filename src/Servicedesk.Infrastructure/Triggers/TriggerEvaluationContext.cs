using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers;

/// All the data the matcher and Block-3 action handlers need to evaluate
/// a single trigger pass. Built once per pass by <see cref="TriggerService"/>
/// after loading the post-mutation ticket snapshot. The triggering event is
/// optional — pure ticket-field updates (no article added) leave it null
/// and only ticket-scoped condition fields can match. <see cref="TriggerId"/>
/// is set by <see cref="TriggerService"/> to the row currently firing so
/// handlers can stamp <c>triggered_by</c> into event metadata + dedup keys.
/// <see cref="Guid.Empty"/> means "no trigger" (matcher-only callers).
/// <see cref="RenderContext"/> is null while the matcher runs and is filled
/// in by <see cref="TriggerService"/> only when a trigger matches and is
/// about to dispatch actions — the matcher does not need template state.
public sealed record TriggerEvaluationContext(
    Guid TicketId,
    Ticket Ticket,
    TicketEvent? TriggeringEvent,
    TriggerChangeSet ChangeSet,
    DateTime UtcNow,
    Guid TriggerId = default)
{
    internal TriggerRenderContext? RenderContext { get; init; }
}
