namespace Servicedesk.Infrastructure.Mail.Attachments;

/// Queue-facing operations on <c>attachment_jobs</c>. The enqueue path lives
/// in <c>IMailMessageRepository.InsertAsync</c> (same transaction as the mail
/// row); this interface exposes what the worker and the health aggregator need.
public interface IAttachmentJobRepository
{
    /// Atomically claims the next Pending job whose <c>next_attempt_utc</c> is
    /// at or before <paramref name="nowUtc"/>. Uses <c>FOR UPDATE SKIP LOCKED</c>
    /// so multiple worker instances can run concurrently without contention.
    /// Returns <c>null</c> when the queue is idle.
    Task<AttachmentJobClaim?> ClaimNextAsync(DateTime nowUtc, CancellationToken ct);

    /// Marks a claimed job as <c>Succeeded</c> and writes the attempt-audit row.
    Task CompleteAsync(long jobId, TimeSpan duration, CancellationToken ct);

    /// Schedules another try: state stays <c>Pending</c>, <c>next_attempt_utc</c>
    /// is bumped, the error is recorded, and an attempt-audit row is appended.
    Task ScheduleRetryAsync(long jobId, DateTime nextAttemptUtc, string error, TimeSpan duration, CancellationToken ct);

    /// Final failure: flips state to <c>DeadLettered</c>. Caller is responsible
    /// for deciding the max-attempts gate (setting-driven).
    Task DeadLetterAsync(long jobId, string error, TimeSpan duration, CancellationToken ct);

    /// How many Pending ingest-jobs are older than <paramref name="threshold"/>.
    /// Used by the health aggregator to flag a growing backlog.
    Task<int> CountPendingOlderThanAsync(TimeSpan threshold, DateTime nowUtc, CancellationToken ct);

    /// How many jobs have been moved to <c>DeadLettered</c>. Non-zero flips the
    /// subsystem to Critical.
    Task<int> CountDeadLetteredAsync(CancellationToken ct);

    /// Re-queues every dead-lettered ingest-job: state back to <c>Pending</c>,
    /// <c>attempt_count</c> reset, <c>next_attempt_utc</c> = now. Returns the
    /// number of rows requeued. Admin-initiated health action.
    Task<int> RequeueDeadLetteredAsync(DateTime nowUtc, CancellationToken ct);

}

/// A claimed job in flight. <see cref="PayloadJson"/> holds the JSON blob the
/// worker parses to locate the attachment in Graph and the target row in
/// <c>attachments</c>.
public sealed record AttachmentJobClaim(
    long JobId,
    string Kind,
    string PayloadJson,
    int AttemptCount);
