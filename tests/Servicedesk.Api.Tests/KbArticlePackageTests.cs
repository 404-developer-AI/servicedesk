using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The KB article package is a file an agent uploads, so the reader is an
/// input boundary: these tests pin the round-trip and every refusal —
/// traversal-shaped asset paths, zip bombs (counted on decompressed bytes,
/// never on the archive's own claims), HTML smuggled in as an asset, and
/// unknown or newer formats.
public sealed class KbArticlePackageTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
    private static readonly KbPackageLimits Limits = new(MaxEntries: 50, MaxAssetBytes: 1_048_576, MaxTotalUncompressedBytes: 4_194_304);

    [Fact]
    public async Task Export_then_import_round_trips_body_and_assets()
    {
        var blobs = new InMemoryBlobStore();
        var articleId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var imageHash = (await blobs.WriteAsync(new MemoryStream(Png))).ContentHash;
        var fileHash = (await blobs.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7 test")))).ContentHash;
        var body =
            $"<p>Step one</p><img src=\"/api/kb/articles/{articleId}/attachments/{imageId}?inline=true\">" +
            $"<p><a href=\"/api/kb/articles/{articleId}/attachments/{fileId}\">Manual</a></p>";

        using var zip = new MemoryStream();
        var skipped = await KbArticlePackage.WriteAsync(zip, new KbPackageExport(
            articleId, "Install the VPN", "nl-BE", body, DateTime.UtcNow,
            new[]
            {
                new KbPackageExportAttachment(imageId, imageHash, "screen shot.png"),
                new KbPackageExportAttachment(fileId, fileHash, "manual.pdf"),
            }), blobs, default);
        Assert.Empty(skipped);

        zip.Position = 0;
        var read = await KbArticlePackage.ReadAsync(zip, Limits, blobs, default);

        Assert.Null(read.Error);
        var content = read.Content!;
        Assert.Equal("Install the VPN", content.Title);
        Assert.Equal("nl-BE", content.LocaleCode);
        Assert.Equal(2, content.Assets.Count);
        Assert.Equal("image/png", content.Assets[0].MimeType);
        Assert.Equal(imageHash, content.Assets[0].Write.ContentHash);
        Assert.DoesNotContain("/api/kb/articles/", content.BodyHtml);

        var urls = content.Assets.ToDictionary(a => a.Path, a => $"/api/kb/articles/new/attachments/{a.Filename}");
        var (html, unresolved) = KbArticlePackage.RewriteForImport(content.BodyHtml, urls);
        Assert.Empty(unresolved);
        Assert.Contains("src=\"/api/kb/articles/new/attachments/screen shot.png\"", html);
        Assert.Contains("href=\"/api/kb/articles/new/attachments/manual.pdf\"", html);
    }

    [Fact]
    public void Export_rewrite_leaves_other_articles_urls_alone()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var aid = Guid.NewGuid();
        var html = $"<img src=\"/api/kb/articles/{other}/attachments/{aid}\">";

        var result = KbArticlePackage.RewriteForExport(html, mine, new Dictionary<Guid, string> { [aid] = "assets/01-x.png" });

        Assert.Equal(html, result);
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("assets/../evil.png")]
    [InlineData("assets/..")]
    [InlineData("/etc/passwd")]
    [InlineData("assets/sub/dir.png")]
    [InlineData("assets\\evil.png")]
    [InlineData("assets/.hidden")]
    [InlineData("C:/evil.png")]
    public async Task Traversal_shaped_asset_paths_are_refused(string path)
    {
        using var zip = BuildPackage(
            Manifest(assets: new[] { new { path, filename = "x.png" } }),
            ("article.html", Encoding.UTF8.GetBytes("<p>x</p>")),
            (path, Png));

        var read = await KbArticlePackage.ReadAsync(zip, Limits, new InMemoryBlobStore(), default);

        Assert.NotNull(read.Error);
        Assert.Null(read.Content);
    }

    [Fact]
    public async Task Zip_bomb_is_rejected_on_decompressed_bytes()
    {
        // 8 MB of zeros compresses to a few KB; the 4 MB total budget must trip.
        var zeros = new byte[8 * 1024 * 1024];
        using var zip = BuildPackage(
            Manifest(assets: new[] { new { path = "assets/a.bin", filename = "a.bin" } }),
            ("article.html", Encoding.UTF8.GetBytes("<p>x</p>")),
            ("assets/a.bin", zeros));
        Assert.True(zip.Length < 100_000);

        var limits = Limits with { MaxAssetBytes = 64 * 1024 * 1024 };
        var read = await KbArticlePackage.ReadAsync(zip, limits, new InMemoryBlobStore(), default);

        Assert.NotNull(read.Error);
        Assert.Contains("expands to more than", read.Error);
    }

    [Fact]
    public async Task Asset_over_the_per_file_cap_is_rejected()
    {
        using var zip = BuildPackage(
            Manifest(assets: new[] { new { path = "assets/big.png", filename = "big.png" } }),
            ("article.html", Encoding.UTF8.GetBytes("<p>x</p>")),
            ("assets/big.png", Png.Concat(new byte[2 * 1024 * 1024]).ToArray()));

        var read = await KbArticlePackage.ReadAsync(zip, Limits, new InMemoryBlobStore(), default);

        Assert.NotNull(read.Error);
        Assert.Contains("larger than", read.Error);
    }

    [Fact]
    public async Task Html_asset_is_refused()
    {
        using var zip = BuildPackage(
            Manifest(assets: new[] { new { path = "assets/page.html", filename = "page.html" } }),
            ("article.html", Encoding.UTF8.GetBytes("<p>x</p>")),
            ("assets/page.html", Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><script>alert(1)</script></body></html>")));

        var read = await KbArticlePackage.ReadAsync(zip, Limits, new InMemoryBlobStore(), default);

        Assert.NotNull(read.Error);
        Assert.Contains("HTML", read.Error);
    }

    [Fact]
    public async Task Too_many_entries_is_rejected()
    {
        var files = Enumerable.Range(0, 60)
            .Select(i => ($"junk/{i}.txt", new byte[] { 1 }))
            .Prepend(("article.html", Encoding.UTF8.GetBytes("<p>x</p>")))
            .ToArray();
        using var zip = BuildPackage(Manifest(), files);

        var read = await KbArticlePackage.ReadAsync(zip, Limits, new InMemoryBlobStore(), default);

        Assert.NotNull(read.Error);
        Assert.Contains("entries", read.Error);
    }

    [Fact]
    public async Task Asset_listed_but_missing_is_rejected()
    {
        using var zip = BuildPackage(
            Manifest(assets: new[] { new { path = "assets/gone.png", filename = "gone.png" } }),
            ("article.html", Encoding.UTF8.GetBytes("<p>x</p>")));

        var read = await KbArticlePackage.ReadAsync(zip, Limits, new InMemoryBlobStore(), default);

        Assert.Contains("missing", read.Error);
    }

    [Fact]
    public async Task Unknown_format_and_newer_version_are_rejected()
    {
        using var foreign = BuildPackage(
            JsonSerializer.Serialize(new { format = "something-else", version = 1, article = new { title = "x" } }),
            ("article.html", Encoding.UTF8.GetBytes("<p>x</p>")));
        var a = await KbArticlePackage.ReadAsync(foreign, Limits, new InMemoryBlobStore(), default);
        Assert.Contains("not a Servicedesk", a.Error);

        using var newer = BuildPackage(
            JsonSerializer.Serialize(new { format = KbArticlePackage.FormatName, version = 99, article = new { title = "x" } }),
            ("article.html", Encoding.UTF8.GetBytes("<p>x</p>")));
        var b = await KbArticlePackage.ReadAsync(newer, Limits, new InMemoryBlobStore(), default);
        Assert.Contains("version 99", b.Error);
    }

    [Fact]
    public async Task Not_a_zip_is_rejected()
    {
        using var junk = new MemoryStream(Encoding.UTF8.GetBytes("definitely not a zip"));

        var read = await KbArticlePackage.ReadAsync(junk, Limits, new InMemoryBlobStore(), default);

        Assert.Contains("not a valid zip", read.Error);
    }

    [Fact]
    public void Import_rewrite_reports_references_the_package_does_not_carry()
    {
        var (html, unresolved) = KbArticlePackage.RewriteForImport(
            "<img src='./assets/a.png'><img src=\"assets/missing.png\">",
            new Dictionary<string, string> { ["assets/a.png"] = "/api/kb/articles/x/attachments/y" });

        Assert.Contains("src='/api/kb/articles/x/attachments/y'", html);
        Assert.Equal(new[] { "assets/missing.png" }, unresolved);
    }

    [Fact]
    public async Task Export_endpoint_requires_a_session()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/kb/articles/{Guid.NewGuid()}/export");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Helpers ───

    private static string Manifest(object[]? assets = null) => JsonSerializer.Serialize(new
    {
        format = KbArticlePackage.FormatName,
        version = KbArticlePackage.FormatVersion,
        article = new { title = "Test article", body = "article.html" },
        assets = assets ?? Array.Empty<object>(),
    });

    private static MemoryStream BuildPackage(string manifestJson, params (string Name, byte[] Bytes)[] files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));
            foreach (var (name, bytes) in files) Add(zip, name, bytes);
        }
        ms.Position = 0;
        return ms;

        static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            s.Write(bytes);
        }
    }

    private sealed class InMemoryBlobStore : IBlobStore
    {
        private readonly Dictionary<string, byte[]> _blobs = new();

        public async Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            _blobs[hash] = bytes;
            return new BlobWriteResult(hash, bytes.Length);
        }

        public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(_blobs.TryGetValue(contentHash, out var b) ? new MemoryStream(b) : null);

        public Task<bool> ExistsAsync(string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.ContainsKey(contentHash));

        public Task<bool> DeleteAsync(string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.Remove(contentHash));
    }
}
