namespace Servicedesk.Infrastructure.Secrets;

/// Single abstraction over the process's secret source. Current implementation
/// reads from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// (env vars, user-secrets, appsettings). Swapping to Key Vault or systemd-creds
/// later is a one-file change — callers never read env vars directly.
public interface ISecretProvider
{
    string? Get(string name);
    string GetRequired(string name);
}
