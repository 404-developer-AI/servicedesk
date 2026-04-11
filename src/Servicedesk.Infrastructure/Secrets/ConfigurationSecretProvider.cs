using Microsoft.Extensions.Configuration;

namespace Servicedesk.Infrastructure.Secrets;

public sealed class ConfigurationSecretProvider : ISecretProvider
{
    private readonly IConfiguration _configuration;

    public ConfigurationSecretProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? Get(string name) => _configuration[name];

    public string GetRequired(string name)
    {
        var value = _configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required secret '{name}' is missing. Configure it via environment variable, user-secrets, or the host secret store.");
        }
        return value;
    }
}
