using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Checklists;

/// Typed snapshot of the Checklists.* settings with the seeded defaults as
/// fallbacks (a missing/mistyped row must never take the feature down).
public sealed record ChecklistRuntimeSettings(
    bool Enabled,
    IReadOnlyList<string> BlockingStateCategories,
    bool LogItemChangesToTimeline,
    int MaxPerTicket,
    int MaxItemsPerChecklist)
{
    public static readonly string[] AllowedBlockingCategories = { "Resolved", "Closed" };

    public bool IsBlockingCategory(string? stateCategory)
        => stateCategory is not null
           && BlockingStateCategories.Contains(stateCategory, StringComparer.OrdinalIgnoreCase);
}

public interface IChecklistSettingsReader
{
    Task<ChecklistRuntimeSettings> GetAsync(CancellationToken ct);
}

public sealed class ChecklistSettingsReader : IChecklistSettingsReader
{
    private readonly ISettingsService _settings;

    public ChecklistSettingsReader(ISettingsService settings) => _settings = settings;

    public async Task<ChecklistRuntimeSettings> GetAsync(CancellationToken ct)
    {
        bool enabled;
        enabled = await _settings.GetOrAsync<bool>(SettingKeys.Checklists.Enabled, true, ct);

        string categoriesRaw;
        categoriesRaw = await _settings.GetOrAsync<string>(SettingKeys.Checklists.BlockingStateCategories, "Resolved,Closed", ct);
        var categories = ParseCategories(categoriesRaw);

        bool log;
        log = await _settings.GetOrAsync<bool>(SettingKeys.Checklists.LogItemChangesToTimeline, false, ct);

        int maxPerTicket;
        maxPerTicket = await _settings.GetOrAsync<int>(SettingKeys.Checklists.MaxPerTicket, 10, ct);
        if (maxPerTicket <= 0) maxPerTicket = 10;
        maxPerTicket = Math.Min(maxPerTicket, ChecklistLimits.HardMaxPerTicket);

        int maxItems;
        maxItems = await _settings.GetOrAsync<int>(SettingKeys.Checklists.MaxItemsPerChecklist, 300, ct);
        if (maxItems <= 0) maxItems = 300;
        maxItems = Math.Min(maxItems, ChecklistLimits.HardMaxItems);

        return new ChecklistRuntimeSettings(enabled, categories, log, maxPerTicket, maxItems);
    }

    /// "Resolved,Closed" → ["Resolved","Closed"]; unknown tokens are dropped
    /// so a typo can never widen the block to Open/Pending statuses.
    public static IReadOnlyList<string> ParseCategories(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => ChecklistRuntimeSettings.AllowedBlockingCategories
                .FirstOrDefault(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .ToList();
    }
}
