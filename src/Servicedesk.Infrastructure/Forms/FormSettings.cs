using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Forms;

public interface IFormSettingsReader
{
    Task<FormRuntimeSettings> GetAsync(CancellationToken ct);
}

/// Typed snapshot of the Forms.* settings, clamped to <see cref="FormLimits"/>
/// and falling back to the seeded defaults (a missing/mistyped row must never
/// take the feature down).
public sealed class FormSettingsReader : IFormSettingsReader
{
    private readonly ISettingsService _settings;

    public FormSettingsReader(ISettingsService settings) => _settings = settings;

    public async Task<FormRuntimeSettings> GetAsync(CancellationToken ct)
    {
        bool enabled;
        try { enabled = await _settings.GetAsync<bool>(SettingKeys.Forms.Enabled, ct); }
        catch { enabled = true; }

        var maxFields = await IntAsync(SettingKeys.Forms.MaxFieldsPerTemplate, 60, 1, FormLimits.HardMaxFields, ct);
        var maxRows = await IntAsync(SettingKeys.Forms.MaxArticleRows, 50, 1, FormLimits.HardMaxArticleRows, ct);
        var maxQty = await IntAsync(SettingKeys.Forms.MaxArticleQuantity, 100_000, 1, (int)FormLimits.HardMaxArticleQuantity, ct);
        var longText = await IntAsync(SettingKeys.Forms.LongTextMaxLength, 4000, 100, FormLimits.HardMaxLongText, ct);
        var perTicket = await IntAsync(SettingKeys.Forms.MaxPerTicket, 50, 1, FormLimits.HardMaxPerTicket, ct);

        return new FormRuntimeSettings(enabled, maxFields, maxRows, maxQty, longText, perTicket);
    }

    private async Task<int> IntAsync(string key, int fallback, int min, int max, CancellationToken ct)
    {
        int v;
        try { v = await _settings.GetAsync<int>(key, ct); }
        catch { v = fallback; }
        if (v <= 0) v = fallback;
        return Math.Clamp(v, min, max);
    }
}
