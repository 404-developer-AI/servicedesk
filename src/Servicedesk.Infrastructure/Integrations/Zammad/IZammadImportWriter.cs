namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Input the importer hands to <see cref="IZammadImportWriter"/> when
/// creating one local ticket from a previously-resolved dry-run record.
/// Every field comes straight from the dry-run snapshot — the importer
/// never re-resolves Zammad-side mappings, so the verdict the admin
/// reviewed on the dry-run is exactly what lands.
public sealed record ZammadImportWriteInput(
    long ZammadTicketId,
    string? ZammadTicketNumber,
    string ZammadTicketTitle,
    System.Guid ContactId,
    System.Guid QueueId,
    System.Guid StatusId,
    System.Guid PriorityId,
    System.Collections.Generic.IReadOnlyList<ZammadArticle> Articles,
    System.DateTime? PendingTillUtc = null);

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
