namespace Servicedesk.Domain.ComposeTemplates;

public sealed record ComposeTemplate(
    Guid Id,
    string Name,
    string? Description,
    string BodyHtml,
    bool IsActive,
    // Empty = available in every queue; otherwise restricted to listed queue ids.
    IReadOnlyList<Guid> QueueIds,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedBy);
