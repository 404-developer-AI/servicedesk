using Servicedesk.Domain.ComposeTemplates;

namespace Servicedesk.Infrastructure.ComposeTemplates;

/// Admin CRUD over <c>compose_templates</c>. The admin UI calls
/// <see cref="ListAsync"/> with <c>includeInactive=true</c>; the editor
/// surfaces (note / reply / mail) call <see cref="ListForQueueAsync"/>
/// so the :: picker only sees templates the agent's current queue may
/// use. Soft-deactivate keeps historical references readable without
/// surfacing the template in new pickers.
public interface IComposeTemplateRepository
{
    Task<IReadOnlyList<ComposeTemplate>> ListAsync(bool includeInactive, CancellationToken ct);

    Task<ComposeTemplate?> GetAsync(Guid id, CancellationToken ct);

    /// Returns active templates that are either unrestricted (empty queue_ids)
    /// or include <paramref name="queueId"/>. When <paramref name="queueId"/>
    /// is <c>null</c> only unrestricted templates are returned — used by the
    /// New-Ticket drawer where no queue is selected yet.
    Task<IReadOnlyList<ComposeTemplate>> ListForQueueAsync(Guid? queueId, CancellationToken ct);

    Task<Guid> CreateAsync(
        string name,
        string? description,
        string bodyHtml,
        IReadOnlyList<Guid> queueIds,
        Guid? createdBy,
        CancellationToken ct);

    Task UpdateAsync(
        Guid id,
        string name,
        string? description,
        string bodyHtml,
        bool isActive,
        IReadOnlyList<Guid> queueIds,
        CancellationToken ct);

    Task<bool> DeactivateAsync(Guid id, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
