namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Input the importer hands to <see cref="IZammadImportWriter"/> when
/// creating one local ticket from a previously-resolved dry-run record.
/// Every field comes straight from the dry-run snapshot — the importer
/// never re-resolves Zammad-side mappings, so the verdict the admin
/// reviewed on the dry-run is exactly what lands.
///
/// Attachment plumbing note (v0.0.41 phase 5): the worker streams each
/// Zammad attachment into <see cref="Servicedesk.Infrastructure.Storage.IBlobStore"/>
/// *before* invoking the writer, then hands the (article-id, attachment-
/// meta, content-hash) tuples in via <see cref="Attachments"/>. Keeping
/// blob-writes outside the writer transaction lets oversized or 404
/// attachments fail/skip without holding the Postgres tx open across
/// multi-MB downloads. The writer just inserts the metadata rows in the
/// same tx as the events they belong to.
public sealed record ZammadImportWriteInput(
    long ZammadTicketId,
    string? ZammadTicketNumber,
    string ZammadTicketTitle,
    System.Guid ContactId,
    System.Guid QueueId,
    System.Guid StatusId,
    System.Guid PriorityId,
    System.Collections.Generic.IReadOnlyList<ZammadArticle> Articles,
    System.Collections.Generic.IReadOnlyList<ZammadImportAttachmentPlan> Attachments,
    System.DateTime? PendingTillUtc = null,
    // Resolved upstream authors for the agent/system articles, keyed by the
    // Zammad user id (created_by_id). Null/empty means "no attribution
    // available" — those events stay anonymous as before. Customer articles
    // are not keyed here; they keep their contact attribution.
    System.Collections.Generic.IReadOnlyDictionary<long, ZammadAuthorAttribution>? Authors = null);

/// Resolved upstream author for one Zammad agent/system article. Mirrors the
/// KB-import author rule: when the upstream agent matches a local user by
/// email we link the real user (<see cref="LocalUserId"/>) so the timeline
/// shows the live identity; otherwise <see cref="DisplayName"/> carries the
/// upstream name as a plain label so imported notes/replies are no longer
/// anonymous. Exactly one of the two is set; both null means the lookup
/// failed and the event stays anonymous.
public sealed record ZammadAuthorAttribution(
    System.Guid? LocalUserId,
    string? DisplayName);

/// One attachment that has already been streamed into the blob store and
/// is ready for the writer to materialise as an <c>attachments</c> row.
/// <see cref="ContentHash"/> is the SHA-256 hex hash returned by
/// <see cref="Servicedesk.Infrastructure.Storage.IBlobStore.WriteAsync"/>;
/// <see cref="SizeBytes"/> is what the blob-store actually observed (which
/// may differ from what Zammad advertised on really old installs that
/// over-reported sizes — we trust the blob-store).
public sealed record ZammadImportAttachmentPlan(
    long ZammadArticleId,
    long ZammadAttachmentId,
    string Filename,
    long SizeBytes,
    string MimeType,
    bool IsInline,
    string? ContentId,
    string ContentHash);

/// What the writer did with one ticket. <c>Result</c> matches the
/// <c>zammad_import_records.result</c> CHECK enum (imported /
/// already_imported / failed) so the worker can write straight through.
public sealed record ZammadImportWriteResult(
    string Result,
    System.Guid? LocalTicketId,
    string? FailureReason);

/// Writes one Zammad ticket into the local schema (tickets + ticket_bodies
/// + ticket_events) reusing the dry-run snapshot as-is. The implementation
/// is responsible for:
/// <list type="bullet">
/// <item>idempotency — the same upstream id is at most one local ticket;</item>
/// <item>company resolution — read the requester's primary company so
/// the ticket lands under the right company filter (or flag
/// <c>awaiting_company_assignment</c> when there is none);</item>
/// <item>article-to-event mapping — Zammad article sender + type drives
/// the local event-type and is_internal flag;</item>
/// <item>"Imported from Zammad" provenance — recorded on every event's
/// metadata and on the ticket row's <c>zammad_ticket_id</c> column.</item>
/// </list>
public interface IZammadImportWriter
{
    Task<ZammadImportWriteResult> WriteAsync(ZammadImportWriteInput input, CancellationToken ct);
}
