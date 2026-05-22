namespace Servicedesk.Infrastructure.Activity;

/// Single write path for non-ticket activity events. Ticket events are
/// mirrored automatically from <c>ticket_events</c> via a Postgres
/// trigger — call <see cref="RecordAsync"/> only for events the trigger
/// cannot see (KB edits, Telavox calls, auth, profile, settings, …).
///
/// Best-effort: the recorder swallows transient errors so a failed write
/// never breaks the calling action. The trigger-based broadcast worker
/// (<c>ActivityListenerWorker</c>) picks up the NOTIFY that Postgres
/// emits for every committed row and pushes it to SignalR.
public interface IActivityRecorder
{
    Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken = default);
}
