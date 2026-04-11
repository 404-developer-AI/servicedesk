using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Infrastructure.Secrets;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class StartupSecretValidatorTests
{
    [Fact]
    public async Task Throws_WhenRequiredSecretMissing()
    {
        var secrets = new StubSecretProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=x;Database=x;Username=x;Password=x",
            // Audit:HashKey intentionally absent
        });
        var validator = new StartupSecretValidator(secrets, NullLogger<StartupSecretValidator>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(default));
        Assert.Contains("Audit:HashKey", ex.Message);
    }

    [Fact]
    public async Task Passes_WhenAllRequiredSecretsPresent()
    {
        var secrets = new StubSecretProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=x;Database=x;Username=x;Password=x",
            ["Audit:HashKey"] = "dGVzdA==",
        });
        var validator = new StartupSecretValidator(secrets, NullLogger<StartupSecretValidator>.Instance);

        await validator.StartAsync(default); // does not throw
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        private readonly Dictionary<string, string?> _values;
        public StubSecretProvider(Dictionary<string, string?> values) { _values = values; }
        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public string GetRequired(string name) => Get(name) ?? throw new InvalidOperationException($"{name} missing");
    }
}
