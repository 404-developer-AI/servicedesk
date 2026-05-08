using Servicedesk.Domain.KnowledgeBase;

namespace Servicedesk.Infrastructure.Persistence.KnowledgeBase;

public interface IKbSectionRepository
{
    Task<IReadOnlyList<KbSection>> ListSectionsAsync(CancellationToken ct);
    Task<KbSection?> GetSectionAsync(Guid id, CancellationToken ct);
    Task<KbSection?> GetSectionBySlugAsync(Guid? parentSectionId, string slug, CancellationToken ct);
    Task<KbSection> CreateSectionAsync(
        Guid? parentSectionId, string slug, string? iconName, int position, Guid actorUserId, CancellationToken ct);
    Task<KbSection?> UpdateSectionAsync(
        Guid id, string slug, string? iconName, int position, Guid actorUserId, CancellationToken ct);
    Task<SectionDeleteResult> DeleteSectionAsync(Guid id, CancellationToken ct);
    Task<KbSection?> MoveSectionAsync(
        Guid id, Guid? newParentSectionId, int newPosition, Guid actorUserId, CancellationToken ct);

    Task<IReadOnlyList<KbSectionTranslation>> ListTranslationsAsync(Guid sectionId, CancellationToken ct);
    Task<IReadOnlyList<KbSectionTranslation>> ListAllTranslationsAsync(CancellationToken ct);
    Task<KbSectionTranslation?> GetTranslationAsync(Guid sectionId, string localeCode, CancellationToken ct);
    Task<KbSectionTranslation> UpsertTranslationAsync(
        Guid sectionId, string localeCode, string title, string? description, CancellationToken ct);
}

public enum SectionDeleteResult { Deleted, NotFound, NotEmpty }
