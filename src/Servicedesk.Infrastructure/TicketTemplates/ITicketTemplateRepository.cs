using Servicedesk.Domain.TicketTemplates;

namespace Servicedesk.Infrastructure.TicketTemplates;

/// Admin CRUD over <c>ticket_templates</c> plus a single agent-facing read
/// (<see cref="ListActiveAsync"/>) the New-Ticket drawer uses to populate its
/// template picker. Hard delete is safe: the table has no inbound FK
/// references — a template is only a starting point, never a live link.
public interface ITicketTemplateRepository
{
    Task<IReadOnlyList<TicketTemplate>> ListAsync(bool includeInactive, CancellationToken ct);

    Task<TicketTemplate?> GetAsync(Guid id, CancellationToken ct);

    /// Active templates only, name-sorted — feeds the New-Ticket drawer picker.
    Task<IReadOnlyList<TicketTemplate>> ListActiveAsync(CancellationToken ct);

    Task<Guid> CreateAsync(TicketTemplate template, CancellationToken ct);

    Task UpdateAsync(TicketTemplate template, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
