using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Phones;

/// Resolves the install-wide default country-code from settings and
/// normalises raw phone input to E.164. Write-paths (contact create /
/// update, Adsolut upsert, mail-intake contact-creation) call this on
/// every persisted phone field so phone_e164 / mobile_phone_e164 stay in
/// sync with phone / mobile_phone.
public interface IContactPhoneNormalizer
{
    Task<string> NormalizeAsync(string? raw, CancellationToken ct = default);

    /// Batch helper: normalises the two contact-table phone columns in
    /// one call, reading the default region once. Use this when a write
    /// path touches both fields together.
    Task<(string PhoneE164, string MobilePhoneE164)> NormalizePairAsync(
        string? phone, string? mobilePhone, CancellationToken ct = default);
}

public sealed class ContactPhoneNormalizer : IContactPhoneNormalizer
{
    /// Fallback when Telavox.DefaultCountryCode is empty or unset, which
    /// is the case on a fresh install before an admin saves the Telavox
    /// integration page. Belgium matches the v0.0.1 customer profile and
    /// keeps phone-search functional out of the box; admins can change
    /// it later from Settings.
    private const string FallbackRegion = "BE";

    private readonly ISettingsService _settings;

    public ContactPhoneNormalizer(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task<string> NormalizeAsync(string? raw, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var region = await ResolveRegionAsync(ct);
        return PhoneE164.Normalize(raw, region);
    }

    public async Task<(string PhoneE164, string MobilePhoneE164)> NormalizePairAsync(
        string? phone, string? mobilePhone, CancellationToken ct = default)
    {
        var region = await ResolveRegionAsync(ct);
        return (PhoneE164.Normalize(phone, region), PhoneE164.Normalize(mobilePhone, region));
    }

    private async Task<string> ResolveRegionAsync(CancellationToken ct)
    {
        var region = await _settings.GetAsync<string>(SettingKeys.Telavox.DefaultCountryCode, ct);
        return string.IsNullOrWhiteSpace(region) ? FallbackRegion : region.Trim().ToUpperInvariant();
    }
}
