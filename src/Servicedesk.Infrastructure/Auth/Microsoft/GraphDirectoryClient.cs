using Azure.Identity;
using Microsoft.Graph;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Auth.Microsoft;

/// Graph SDK implementation. Builds a fresh <c>GraphServiceClient</c> per
/// call so tenant-id / client-id / client-secret changes from the Settings
/// page take effect without an app restart — same pattern as
/// <c>GraphMailClient.BuildClientAsync</c>.
public sealed class GraphDirectoryClient : IGraphDirectoryClient
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;

    public GraphDirectoryClient(ISettingsService settings, IProtectedSecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public async Task<GraphUserStatus?> GetUserStatusAsync(string oid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oid))
        {
            return null;
        }

        var graph = await BuildClientAsync(ct);

        try
        {
            var user = await graph.Users[oid].GetAsync(config =>
            {
                config.QueryParameters.Select = new[]
                {
                    "id", "accountEnabled", "userPrincipalName", "displayName", "mail"
                };
            }, cancellationToken: ct);

            if (user is null || string.IsNullOrWhiteSpace(user.Id))
            {
                return null;
            }

            return new GraphUserStatus(
                Oid: user.Id,
                AccountEnabled: user.AccountEnabled ?? false,
                UserPrincipalName: user.UserPrincipalName,
                DisplayName: user.DisplayName,
                Mail: user.Mail);
        }
        catch (global::Microsoft.Graph.Models.ODataErrors.ODataError err)
            when (err.ResponseStatusCode == 404)
        {
            // User was deleted from the tenant (or never existed). Treat
            // as "no longer reachable" — caller rejects the login.
            return null;
        }
    }

    public async Task<IReadOnlyList<GraphUserStatus>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default)
    {
        var effectiveLimit = limit <= 0 ? 20 : Math.Min(limit, 50);
        var graph = await BuildClientAsync(ct);
        var trimmed = (query ?? string.Empty).Trim();

        var response = await graph.Users.GetAsync(config =>
        {
            // $search requires "eventual" consistency and supports the
            // "displayName:term" / "mail:term" / "userPrincipalName:term"
            // syntax. Graph also requires $orderby pairing when $search is
            // used on certain tenants — we stay on $search-only and let
            // Graph pick its default order.
            config.QueryParameters.Top = effectiveLimit;
            config.QueryParameters.Select = new[]
            {
                "id", "accountEnabled", "userPrincipalName", "displayName", "mail"
            };

            if (trimmed.Length > 0)
            {
                // Graph's $search uses quoted phrase expressions per field.
                // Escape the user's double-quotes and build a three-way OR
                // so a query like "ali" hits displayName AND mail AND upn.
                var escaped = trimmed.Replace("\"", "\\\"", StringComparison.Ordinal);
                config.QueryParameters.Search =
                    $"\"displayName:{escaped}\" OR \"userPrincipalName:{escaped}\" OR \"mail:{escaped}\"";
                config.Headers.Add("ConsistencyLevel", "eventual");
            }
            else
            {
                config.QueryParameters.Orderby = new[] { "displayName" };
            }
        }, cancellationToken: ct);

        var results = new List<GraphUserStatus>();
        if (response?.Value is null) return results;

        foreach (var u in response.Value)
        {
            if (string.IsNullOrWhiteSpace(u.Id)) continue;
            results.Add(new GraphUserStatus(
                Oid: u.Id,
                AccountEnabled: u.AccountEnabled ?? false,
                UserPrincipalName: u.UserPrincipalName,
                DisplayName: u.DisplayName,
                Mail: u.Mail));
        }
        return results;
    }

    private async Task<GraphServiceClient> BuildClientAsync(CancellationToken ct)
    {
        var tenantId = await _settings.GetAsync<string>(SettingKeys.Graph.TenantId, ct);
        var clientId = await _settings.GetAsync<string>(SettingKeys.Graph.ClientId, ct);
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.GraphClientSecret, ct);

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Microsoft Graph is not fully configured. Set Graph.TenantId, Graph.ClientId, and the client secret via Settings.");
        }

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        return new GraphServiceClient(credential, Scopes);
    }
}
