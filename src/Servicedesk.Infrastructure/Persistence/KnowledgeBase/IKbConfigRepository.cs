using Servicedesk.Domain.KnowledgeBase;

namespace Servicedesk.Infrastructure.Persistence.KnowledgeBase;

public interface IKbConfigRepository
{
    Task<KnowledgeBaseConfig> GetConfigAsync(CancellationToken ct);
    Task<KnowledgeBaseConfig> UpdateConfigAsync(bool isActive, string defaultLocaleCode, CancellationToken ct);

    Task<IReadOnlyList<KbLocale>> ListLocalesAsync(bool includeInactive, CancellationToken ct);
    Task<KbLocale?> GetLocaleAsync(string code, CancellationToken ct);
    Task<KbLocale> UpsertLocaleAsync(KbLocale locale, CancellationToken ct);
    Task<bool> RemoveLocaleAsync(string code, CancellationToken ct);
    Task<bool> LocaleHasTranslationsAsync(string code, CancellationToken ct);
}
