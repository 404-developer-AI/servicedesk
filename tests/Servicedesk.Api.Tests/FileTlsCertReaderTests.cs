using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Servicedesk.Infrastructure.Health;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Regression test for v0.0.50. v0.0.18 through v0.0.49 used
/// X509Certificate2.CreateFromPemFile(path) with a single argument, which
/// requires the file to contain BOTH a CERTIFICATE block AND a PRIVATE KEY
/// block. Let's Encrypt's fullchain.pem only has the cert chain (the key
/// lives in privkey.pem, 0600 root-only). The parse therefore threw
/// CryptographicException, the broad catch swallowed it, and the tls-cert
/// health card permanently showed "Expected at .../fullchain.pem — not
/// readable" on every install.
public sealed class FileTlsCertReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public FileTlsCertReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sd-cert-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Read_KeyLessFullchainPem_ReturnsCertInfo()
    {
        // Self-signed cert via ephemeral ECDsa, exported as PEM with NO
        // accompanying private key — exactly the shape Let's Encrypt's
        // fullchain.pem ships. CreateFromPemFile(path) would throw here;
        // CreateFromPem(File.ReadAllText(path)) is the correct API.
        var notAfter = DateTimeOffset.UtcNow.AddDays(42);
        var domain = "test.example.com";
        var pem = BuildSelfSignedPemCertOnly(domain, notAfter);
        var domainDir = Path.Combine(_tmpDir, domain);
        Directory.CreateDirectory(domainDir);
        File.WriteAllText(Path.Combine(domainDir, "fullchain.pem"), pem);

        var reader = new FileTlsCertReader(Options.Create(new TlsCertHealthOptions
        {
            Domain = domain,
            CertDirectory = _tmpDir,
        }));

        var info = reader.Read();

        Assert.NotNull(info);
        Assert.Contains(domain, info!.Subject);
        // Round-trip through cert formats trims sub-second precision; allow 2s slack.
        Assert.True(Math.Abs((info.NotAfterUtc - notAfter.UtcDateTime).TotalSeconds) < 2);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        var reader = new FileTlsCertReader(Options.Create(new TlsCertHealthOptions
        {
            Domain = "absent.example.com",
            CertDirectory = _tmpDir,
        }));

        Assert.Null(reader.Read());
    }

    [Fact]
    public void Read_EmptyDomain_ReturnsNullWithoutHittingFs()
    {
        var reader = new FileTlsCertReader(Options.Create(new TlsCertHealthOptions
        {
            Domain = "",
            CertDirectory = "/this/path/must/not/be/touched",
        }));

        Assert.Null(reader.Read());
    }

    private static string BuildSelfSignedPemCertOnly(string commonName, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), notAfter);
        return cert.ExportCertificatePem();
    }
}
