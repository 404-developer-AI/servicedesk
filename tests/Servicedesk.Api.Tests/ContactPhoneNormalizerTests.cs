using Servicedesk.Infrastructure.Phones;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.34 — verifies the settings-aware wrapper over PhoneE164. The
/// concern split is deliberate: the static utility takes a region, the
/// service resolves the region from Telavox.DefaultCountryCode once per
/// call. These tests pin the fallback (empty setting → "BE"), the
/// upper-casing (be → BE), and the pair-helper.
public sealed class ContactPhoneNormalizerTests
{
    [Fact]
    public async Task Empty_input_returns_empty_without_reading_settings()
    {
        var settings = new RecordingSettingsStub("BE");
        var normalizer = new ContactPhoneNormalizer(settings);

        Assert.Equal(string.Empty, await normalizer.NormalizeAsync(null));
        Assert.Equal(string.Empty, await normalizer.NormalizeAsync(""));
        Assert.Equal(string.Empty, await normalizer.NormalizeAsync("   "));
        Assert.Equal(0, settings.ReadCount);
    }

    [Fact]
    public async Task Local_format_uses_settings_region()
    {
        var normalizer = new ContactPhoneNormalizer(new RecordingSettingsStub("BE"));
        Assert.Equal("+32498123456", await normalizer.NormalizeAsync("0498 12 34 56"));
    }

    [Fact]
    public async Task Switching_region_changes_outcome()
    {
        var be = new ContactPhoneNormalizer(new RecordingSettingsStub("BE"));
        var nl = new ContactPhoneNormalizer(new RecordingSettingsStub("NL"));

        // "0612345678" parses as a valid NL mobile and an invalid BE number.
        Assert.Equal("+31612345678", await nl.NormalizeAsync("0612345678"));
        Assert.Equal(string.Empty, await be.NormalizeAsync("0612345678"));
    }

    [Fact]
    public async Task Empty_setting_falls_back_to_BE()
    {
        var normalizer = new ContactPhoneNormalizer(new RecordingSettingsStub(string.Empty));
        Assert.Equal("+32498123456", await normalizer.NormalizeAsync("0498 12 34 56"));
    }

    [Fact]
    public async Task Lowercase_setting_is_upper_cased_before_parsing()
    {
        // libphonenumber requires the region code in upper case.
        var normalizer = new ContactPhoneNormalizer(new RecordingSettingsStub("be"));
        Assert.Equal("+32498123456", await normalizer.NormalizeAsync("0498 12 34 56"));
    }

    [Fact]
    public async Task Normalize_pair_returns_both()
    {
        var normalizer = new ContactPhoneNormalizer(new RecordingSettingsStub("BE"));
        var (phone, mobile) = await normalizer.NormalizePairAsync("0498 11 22 33", "0473 44 55 66");
        Assert.Equal("+32498112233", phone);
        Assert.Equal("+32473445566", mobile);
    }

    [Fact]
    public async Task Normalize_pair_handles_one_invalid_side()
    {
        var normalizer = new ContactPhoneNormalizer(new RecordingSettingsStub("BE"));
        var (phone, mobile) = await normalizer.NormalizePairAsync("0498 11 22 33", "garbage");
        Assert.Equal("+32498112233", phone);
        Assert.Equal(string.Empty, mobile);
    }

    private sealed class RecordingSettingsStub : ISettingsService
    {
        private readonly string _value;
        public int ReadCount { get; private set; }

        public RecordingSettingsStub(string value) => _value = value;

        public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult((T)(object)_value);
        }

        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }
}
