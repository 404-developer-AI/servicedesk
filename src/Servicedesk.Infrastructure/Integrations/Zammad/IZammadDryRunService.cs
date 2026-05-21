namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Service surface for the v0.0.41 phase 3 dry-run engine.
///
/// Start flow: the API endpoint calls <see cref="StartDryRunAsync"/> with
/// the picker's current selection + filters. The service persists a row
/// in <c>zammad_import_runs</c> with status='pending' and enqueues the
/// run id on the background worker's channel. The endpoint returns the
/// run id immediately so the SPA can navigate to the run-detail page and
/// start polling for progress.
///
/// Read flow: <see cref="GetRunsAsync"/> drives the runs-list page;
/// <see cref="GetRunAsync"/> + <see cref="GetRecordsAsync"/> drive the
/// run-detail page. <see cref="GetRunAsync"/> reads <c>totals</c> on
/// every poll so progress updates without a separate endpoint.
///
/// Cancel flow: <see cref="CancelRunAsync"/> sets the status column to
/// 'cancelled'; the worker checks the column between tickets and exits
/// the loop on next poll.
public interface IZammadDryRunService
{
    Task<Guid> StartDryRunAsync(
        ZammadImportSourceFilter filter,
        Guid? startedByUserId,
        CancellationToken ct);

    Task<IReadOnlyList<ZammadImportRunSummary>> GetRunsAsync(
        int limit,
        CancellationToken ct);

    Task<ZammadImportRunDetail?> GetRunAsync(Guid runId, CancellationToken ct);

    Task<ZammadImportRecordPage> GetRecordsAsync(
        Guid runId,
        Guid? cursor,
        int limit,
        string? resultFilter,
        CancellationToken ct);

    /// Marks the run as cancelled. Returns false when the run is already
    /// in a terminal state (completed / failed / cancelled).
    Task<bool> CancelRunAsync(Guid runId, CancellationToken ct);

    /// Re-evaluate one or more existing records against the current
    /// mapping + contacts state. Each record is re-fetched from Zammad,
    /// passed through <see cref="IZammadTicketResolver"/>, and the new
    /// verdict overwrites the row. The run's totals JSONB is bumped by
    /// the diff so the runs-list + summary cards stay accurate.
    ///
    /// Returns the number of records actually re-evaluated. Records
    /// belonging to a different run-id or that don't exist are silently
    /// skipped — callers pass ids derived from a record-listing query
    /// that already filtered by run.
    Task<int> RecheckRecordsAsync(
        Guid runId,
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken ct);

    /// Promote a completed dry-run to a real import. Validates that the
    /// referenced dry-run is in a state that can be promoted — completed,
    /// within the retention window, with at least one mapped record —
    /// then writes a new run row with <c>kind='import'</c> referencing
    /// the dry-run's id in the source filter, enqueues the runId, and
    /// returns it.
    ///
    /// On guard-rail rejection (dry-run missing, wrong kind, not
    /// completed, expired, or no mapped records) returns a result whose
    /// <c>RunId</c> is null and <c>ErrorCode</c> identifies which rule
    /// fired so the API layer can return a structured 4xx.
    Task<ZammadImportStartResult> StartImportFromDryRunAsync(
        Guid dryRunId,
        Guid? startedByUserId,
        CancellationToken ct);
}

/// Outcome of <see cref="IZammadDryRunService.StartImportFromDryRunAsync"/>.
/// Success carries a non-null <see cref="RunId"/>; failure carries a
/// short <see cref="ErrorCode"/> the API layer maps onto a 4xx.
public sealed record ZammadImportStartResult(
    Guid? RunId,
    string? ErrorCode,
    string? ErrorMessage);

/// Hand-off channel between the API endpoint that starts a run and the
/// background worker that processes it. Thin abstraction over
/// <c>System.Threading.Channels.Channel&lt;Guid&gt;</c> so the worker can be
/// unit-tested without spinning up the real channel.
public interface IZammadDryRunQueue
{
    /// Enqueues a run-id. The worker reads with WaitToReadAsync — the
    /// queue keeps a small bounded buffer so a runaway admin can't pile
    /// up infinite work; the persisted run row carries the source-of-
    /// truth so a queue overflow surfaces as a 503 to the admin instead
    /// of silently dropping work.
    bool TryEnqueue(Guid runId);

    /// Awaitable read used by the worker's main loop.
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}
