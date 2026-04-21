using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Servicedesk.Infrastructure.Health;

/// Reads the Let's Encrypt <c>fullchain.pem</c> for the current install so the
/// health aggregator can surface its expiry. Extracted behind an interface so
/// tests can stub the filesystem — the concrete reader intentionally does no
/// network or certbot work.
public interface ITlsCertReader
{
    /// Returns <c>null</c> when the cert file does not exist or cannot be read.
    /// A valid read produces only the two fields the health card needs — the
    /// subject (for the "Domain" detail row) and the not-after date. We never
    /// expose the full chain or private key.
    TlsCertInfo? Read();
}

public sealed record TlsCertInfo(string Subject, DateTime NotAfterUtc);

public sealed class FileTlsCertReader : ITlsCertReader
{
    private readonly IOptions<TlsCertHealthOptions> _options;

    public FileTlsCertReader(IOptions<TlsCertHealthOptions> options)
    {
        _options = options;
    }

    public TlsCertInfo? Read()
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.Domain))
        {
            return null;
        }

        var path = Path.Combine(opts.CertDirectory, opts.Domain, "fullchain.pem");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // X509Certificate2.CreateFromPemFile reads the LEAF — which is
            // what we want for notAfter. fullchain.pem starts with the leaf
            // and appends intermediates; the loader picks the first one.
            using var cert = X509Certificate2.CreateFromPemFile(path);
            return new TlsCertInfo(cert.Subject, cert.NotAfter.ToUniversalTime());
        }
        catch
        {
            // Malformed PEM or permission-denied → treat as "no cert" so the
            // card surfaces the same "not found" state instead of throwing
            // and tanking the whole health report.
            return null;
        }
    }
}
