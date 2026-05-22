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
    /// <para>
    /// v0.0.42: <paramref name="statusId"/> further filters by status scope.
    /// A template with empty <c>status_ids</c> matches every status; otherwise
    /// the status must appear in the array. <c>null</c> skips the status
    /// filter entirely (preserves pre-v0.0.42 semantics for callers that
    /// don't know the ticket's status).
    /// </para>
    Task<IReadOnlyList<ComposeTemplate>> ListForQueueAsync(Guid? queueId, Guid? statusId, CancellationToken ct);

    /// v0.0.42 — Picks the single auto-insert template that matches the
    /// ticket's queue + status. Returns <c>null</c> when no candidate
    /// exists. Tie-breaker is most-recently-updated.
    Task<ComposeTemplate?> FindAutoInsertForNoteAsync(Guid queueId, Guid statusId, CancellationToken ct);

    Task<Guid> CreateAsync(
        string name,
        string? description,
        string bodyHtml,
        IReadOnlyList<Guid> queueIds,
        IReadOnlyList<Guid> statusIds,
        bool autoInsertOnNote,
        Guid? linkedSurveyId,
        Guid? createdBy,
        CancellationToken ct);

    Task UpdateAsync(
        Guid id,
        string name,
        string? description,
        string bodyHtml,
        bool isActive,
        IReadOnlyList<Guid> queueIds,
        IReadOnlyList<Guid> statusIds,
        bool autoInsertOnNote,
        Guid? linkedSurveyId,
        CancellationToken ct);

    Task<bool> DeactivateAsync(Guid id, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
