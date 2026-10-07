using System.Security.Cryptography;
using System.Text;
using Microsoft.Graph;

namespace Servicedesk.Infrastructure.Mail.Graph;

/// Holds one <see cref="GraphServiceClient"/> per owning singleton, keyed on
/// (tenant id, client id, secret hash). Building a client per call threw away
/// the Azure.Identity token cache and the HTTP connection pool on every Graph
/// action (one token POST + TLS handshake each time). The client is rebuilt
/// only when one of the three credentials changes, so a secret rotation on the
/// Settings page still takes effect without a restart. The previous client is
/// deliberately not disposed: in-flight calls may still hold it, and rotation
/// is rare enough that leaving it to the GC is harmless.
internal sealed class GraphServiceClientCache
{
    private readonly Func<string, string, string, GraphServiceClient> _factory;
    private readonly object _gate = new();
    private string? _key;
    private GraphServiceClient? _client;

    public GraphServiceClientCache(Func<string, string, string, GraphServiceClient> factory)
    {
        _factory = factory;
    }

    public GraphServiceClient Get(string tenantId, string clientId, string clientSecret)
    {
        var key = BuildKey(tenantId, clientId, clientSecret);
        lock (_gate)
        {
            if (_client is null || !string.Equals(_key, key, StringComparison.Ordinal))
            {
                _client = _factory(tenantId, clientId, clientSecret);
                _key = key;
            }
            return _client;
        }
    }

    // The secret itself is never kept as a key — only its SHA-256.
    private static string BuildKey(string tenantId, string clientId, string clientSecret)
    {
        var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret)));
        return $"{tenantId}\n{clientId}\n{secretHash}";
    }
}
