using Servicedesk.Domain.TaggingMailboxes;

namespace Servicedesk.Infrastructure.TaggingMailboxes;

/// Dapper-backed repository for the `tagging_mailboxes` table. Small,
/// admin-curated list; reads are cheap and uncached. Email is stored
/// lower-cased so the unique index (and mention de-dup) are case-insensitive.
public interface ITaggingMailboxRepository
{
    /// Full list for the Settings → Users management card, ordered by name.
    Task<IReadOnlyList<TaggingMailbox>> ListAsync(CancellationToken ct);

    Task<TaggingMailbox?> GetAsync(Guid id, CancellationToken ct);

    /// Active-only typeahead for the @@-mention picker. Matches name OR email.
    Task<IReadOnlyList<TaggingMailbox>> SearchActiveAsync(string? search, int limit, CancellationToken ct);

    /// Resolves the supplied ids to their active rows. Used both to filter an
    /// incoming mention payload (drop unknown / inactive ids) and to fetch the
    /// addresses the notification service mails. Inactive ids are dropped.
    Task<IReadOnlyList<TaggingMailbox>> ResolveActiveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// Returns the new id. Throws Npgsql 23505 on a duplicate (case-insensitive) email.
    Task<Guid> CreateAsync(string name, string email, bool isActive, CancellationToken ct);

    /// Returns false if the id no longer exists. Throws 23505 on email collision.
    Task<bool> UpdateAsync(Guid id, string name, string email, bool isActive, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
