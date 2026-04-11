using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class Argon2idPasswordHasherTests
{
    private static Argon2idPasswordHasher CreateHasher(InMemorySettingsService? settings = null)
    {
        settings ??= new InMemorySettingsService();
        // Keep it fast for CI. Real defaults are 64MiB/3it.
        settings.Set(SettingKeys.Security.PasswordArgon2MemoryKb, "8192");
        settings.Set(SettingKeys.Security.PasswordArgon2Iterations, "1");
        settings.Set(SettingKeys.Security.PasswordArgon2Parallelism, "1");
        return new Argon2idPasswordHasher(settings);
    }

    [Fact]
    public void Hash_And_Verify_Roundtrip_Succeeds()
    {
        var hasher = CreateHasher();
        var encoded = hasher.Hash("correct-horse-battery-staple");

        Assert.StartsWith("$argon2id$v=19$", encoded);
        Assert.True(hasher.Verify(encoded, "correct-horse-battery-staple", out var rehash));
        Assert.False(rehash);
    }

    [Fact]
    public void Verify_With_Wrong_Password_Fails()
    {
        var hasher = CreateHasher();
        var encoded = hasher.Hash("correct-horse-battery-staple");

        Assert.False(hasher.Verify(encoded, "wrong-password", out _));
    }

    [Fact]
    public void Verify_Flags_Rehash_When_Parameters_Drift()
    {
        var settings = new InMemorySettingsService();
        var hasher = CreateHasher(settings);
        var encoded = hasher.Hash("correct-horse-battery-staple");

        // Bump iterations — simulates admin raising Argon2 cost via the settings page.
        settings.Set(SettingKeys.Security.PasswordArgon2Iterations, "2");

        Assert.True(hasher.Verify(encoded, "correct-horse-battery-staple", out var rehash));
        Assert.True(rehash);
    }

    [Fact]
    public void Verify_Returns_False_On_Malformed_Hash()
    {
        var hasher = CreateHasher();
        Assert.False(hasher.Verify("not-a-hash", "anything", out _));
        Assert.False(hasher.Verify("$argon2id$v=19$m=8192,t=1,p=1$bad!$bad!", "anything", out _));
    }
}
