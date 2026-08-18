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
        try { enabled = await _settings.GetAsync<bool>(SettingKeys.Checklists.Enabled, ct); }
        catch { enabled = true; }

        string categoriesRaw;
        try { categoriesRaw = await _settings.GetAsync<string>(SettingKeys.Checklists.BlockingStateCategories, ct); }
        catch { categoriesRaw = "Resolved,Closed"; }
        var categories = ParseCategories(categoriesRaw);

        bool log;
        try { log = await _settings.GetAsync<bool>(SettingKeys.Checklists.LogItemChangesToTimeline, ct); }
        catch { log = false; }

        int maxPerTicket;
        try { maxPerTicket = await _settings.GetAsync<int>(SettingKeys.Checklists.MaxPerTicket, ct); }
        catch { maxPerTicket = 10; }
        if (maxPerTicket <= 0) maxPerTicket = 10;
        maxPerTicket = Math.Min(maxPerTicket, ChecklistLimits.HardMaxPerTicket);

        int maxItems;
        try { maxItems = await _settings.GetAsync<int>(SettingKeys.Checklists.MaxItemsPerChecklist, ct); }
        catch { maxItems = 300; }
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
