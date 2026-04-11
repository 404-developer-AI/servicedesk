using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Secrets;

/// Fail-fast check at boot: any required secret that is missing throws before
/// the web host starts accepting requests. Never logs secret values.
public sealed class StartupSecretValidator : IHostedService
{
    private static readonly string[] RequiredSecrets =
    {
        "ConnectionStrings:Postgres",
        "Audit:HashKey",
    };

    private readonly ISecretProvider _secrets;
    private readonly ILogger<StartupSecretValidator> _logger;

    public StartupSecretValidator(ISecretProvider secrets, ILogger<StartupSecretValidator> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        foreach (var key in RequiredSecrets)
        {
            if (string.IsNullOrWhiteSpace(_secrets.Get(key)))
            {
                missing.Add(key);
            }
        }

        if (missing.Count > 0)
        {
            var list = string.Join(", ", missing);
            throw new InvalidOperationException(
                $"Cannot start: required secret(s) missing: {list}. " +
                "Set them via environment variable (production) or `dotnet user-secrets` (development).");
        }

        _logger.LogInformation("Startup secret validation passed ({Count} required secrets present).", RequiredSecrets.Length);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
