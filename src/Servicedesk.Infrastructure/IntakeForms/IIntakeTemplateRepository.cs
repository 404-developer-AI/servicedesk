using Servicedesk.Domain.IntakeForms;

namespace Servicedesk.Infrastructure.IntakeForms;

/// Admin CRUD for intake templates. A template owns its ordered questions
/// and (for dropdown questions) their options. All updates replace the
/// full child-collection inside a single transaction so the designer can
/// reorder, add, and remove questions in one save without the repository
/// juggling partial edits.
public interface IIntakeTemplateRepository
{
    Task<IReadOnlyList<IntakeTemplate>> ListAsync(bool includeInactive, CancellationToken ct);
    Task<IntakeTemplate?> GetAsync(Guid id, CancellationToken ct);
    Task<Guid> CreateAsync(string name, string? description, IReadOnlyList<IntakeQuestionInput> questions, Guid? createdBy, CancellationToken ct);
    Task UpdateAsync(Guid id, string name, string? description, bool isActive, IReadOnlyList<IntakeQuestionInput> questions, CancellationToken ct);

    /// Soft-deactivate. Hard delete is refused if any instance references the
    /// template — preserves audit-trail readability for sent forms.
    Task<bool> DeactivateAsync(Guid id, CancellationToken ct);

    Task<bool> IsReferencedByInstancesAsync(Guid id, CancellationToken ct);
}

public sealed record IntakeQuestionInput(
    int SortOrder,
    IntakeQuestionType Type,
    string Label,
    string? HelpText,
    bool IsRequired,
    string? DefaultValue,
    string? DefaultToken,
    IReadOnlyList<IntakeQuestionOptionInput> Options);

public sealed record IntakeQuestionOptionInput(
    int SortOrder,
    string Value,
    string Label);
