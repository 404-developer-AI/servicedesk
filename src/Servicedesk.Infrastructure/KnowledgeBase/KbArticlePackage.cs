using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.KnowledgeBase;

/// The single-article transfer package: a zip carrying
/// <c>manifest.json</c>, the body as <c>article.html</c> and every
/// attachment under <c>assets/</c>. In the package the body references its
/// files by relative path (<c>&lt;img src="assets/01-step.png"&gt;</c>), so
/// a package can be authored by hand and moved between installs; on import
/// the paths are rewritten to the new article's attachment URLs.
///
/// Reading is defensive: the zip is never extracted to disk, entry names are
/// only ever used as lookup keys (no filesystem path is built from them),
/// only entries named in the manifest are read, and every decompressed byte
/// is counted against a per-entry and a total cap so a zip bomb aborts early
/// regardless of the sizes the archive claims.
public static partial class KbArticlePackage
{
    public const string FormatName = "servicedesk.kb-article";
    public const int FormatVersion = 1;
    public const string ManifestEntry = "manifest.json";
    public const string BodyEntry = "article.html";

    // Structural caps of the format itself, not tunables: a manifest is a few
    // hundred bytes per asset and a KB body is text. Asset sizes, entry count
    // and the total budget come from settings via KbPackageLimits.
    private const long MaxManifestBytes = 262_144;
    private const long MaxBodyBytes = 5_242_880;
    private const int MaxTitleLength = 250;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ───────────────────────────── Export ─────────────────────────────

    /// Writes the package for one article into <paramref name="output"/>.
    /// <paramref name="bodyHtml"/> is the stored (already sanitised) body;
    /// every reference to one of the article's own attachment URLs is
    /// rewritten to the asset's package path. Attachments whose blob is gone
    /// are skipped and named in the returned list.
    public static async Task<IReadOnlyList<string>> WriteAsync(
        Stream output, KbPackageExport export, IBlobStore blobs, CancellationToken ct)
    {
        var skipped = new List<string>();
        var pathById = new Dictionary<Guid, string>();
        var manifestAssets = new List<KbPackageManifestAsset>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = 1;
            foreach (var att in export.Attachments)
            {
                await using var source = await blobs.OpenReadAsync(att.ContentHash, ct);
                if (source is null)
                {
                    skipped.Add(att.Filename);
                    continue;
                }

                var path = "assets/" + UniqueAssetName(index++, att.Filename, usedNames);
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                await using (var target = entry.Open())
                {
                    await source.CopyToAsync(target, ct);
                }
                pathById[att.Id] = path;
                manifestAssets.Add(new KbPackageManifestAsset(path, att.Filename));
            }

            var body = RewriteForExport(export.BodyHtml, export.ArticleId, pathById);
            await WriteTextEntryAsync(zip, BodyEntry, body, ct);

            var manifest = new KbPackageManifest(
                FormatName, FormatVersion, export.ExportedUtc,
                new KbPackageManifestArticle(export.Title, export.LocaleCode, BodyEntry),
                manifestAssets);
            await WriteTextEntryAsync(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonOptions), ct);
        }

        return skipped;
    }

    /// Replaces this article's attachment URLs (with or without a query
    /// string such as <c>?inline=true</c>) by the asset's package path.
    /// URLs of attachments that were not exported are left untouched.
    public static string RewriteForExport(
        string bodyHtml, Guid articleId, IReadOnlyDictionary<Guid, string> pathById)
    {
        if (string.IsNullOrEmpty(bodyHtml) || pathById.Count == 0) return bodyHtml ?? string.Empty;
        var prefix = $"/api/kb/articles/{articleId}/attachments/";
        return AttachmentUrlPattern().Replace(bodyHtml, m =>
        {
            if (!m.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return m.Value;
            return Guid.TryParse(m.Groups["aid"].Value, out var aid) && pathById.TryGetValue(aid, out var path)
                ? path
                : m.Value;
        });
    }

    // ───────────────────────────── Import ─────────────────────────────

    /// Validates the package and stores every manifest asset in the blob
    /// store. Nothing is written to the database here — the caller creates
    /// the article only after the whole package proved valid. Blobs of a
    /// package that fails half-way stay unreferenced and are reclaimed by
    /// the blob GC, like any aborted upload.
    public static async Task<KbPackageReadResult> ReadAsync(
        Stream zipStream, KbPackageLimits limits, IBlobStore blobs, CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return KbPackageReadResult.Fail("The file is not a valid zip archive.");
        }

        using (zip)
        {
            if (zip.Entries.Count > limits.MaxEntries)
                return KbPackageReadResult.Fail($"The package has more than {limits.MaxEntries} entries.");

            // Entry names are lookup keys only. Duplicates are refused so the
            // manifest can't point at one entry while a viewer sees another.
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                if (!entries.TryAdd(e.FullName, e))
                    return KbPackageReadResult.Fail("The package contains duplicate entry names.");
            }

            var budget = new DecompressionBudget(limits.MaxTotalUncompressedBytes);

            if (!entries.TryGetValue(ManifestEntry, out var manifestEntry))
                return KbPackageReadResult.Fail("manifest.json is missing.");

            KbPackageManifest? manifest;
            try
            {
                var manifestJson = await ReadTextAsync(manifestEntry, MaxManifestBytes, budget, ct);
                manifest = JsonSerializer.Deserialize<KbPackageManifest>(manifestJson, JsonOptions);
            }
            catch (PackageLimitException ex) { return KbPackageReadResult.Fail(ex.Message); }
            catch (JsonException) { return KbPackageReadResult.Fail("manifest.json is not valid JSON."); }
            catch (InvalidDataException) { return KbPackageReadResult.Fail("manifest.json could not be decompressed."); }

            if (manifest is null || !string.Equals(manifest.Format, FormatName, StringComparison.Ordinal))
                return KbPackageReadResult.Fail("This is not a Servicedesk knowledge base article package.");
            if (manifest.Version > FormatVersion)
                return KbPackageReadResult.Fail(
                    $"The package uses format version {manifest.Version}; this install reads up to version {FormatVersion}. Update the app first.");
            if (manifest.Version < 1)
                return KbPackageReadResult.Fail("The package format version is invalid.");

            var title = manifest.Article?.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                return KbPackageReadResult.Fail("The manifest has no article title.");
            if (title.Length > MaxTitleLength)
                return KbPackageReadResult.Fail($"The article title is longer than {MaxTitleLength} characters.");

            var bodyName = string.IsNullOrWhiteSpace(manifest.Article!.Body) ? BodyEntry : manifest.Article.Body;
            if (!entries.TryGetValue(bodyName, out var bodyEntry))
                return KbPackageReadResult.Fail($"The article body '{bodyName}' is missing from the package.");

            string bodyHtml;
            try
            {
                bodyHtml = await ReadTextAsync(bodyEntry, MaxBodyBytes, budget, ct);
            }
            catch (PackageLimitException ex) { return KbPackageReadResult.Fail(ex.Message); }
            catch (InvalidDataException) { return KbPackageReadResult.Fail("The article body could not be decompressed."); }

            var assets = new List<KbPackageAsset>();
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in manifest.Assets ?? Array.Empty<KbPackageManifestAsset>())
            {
                var path = asset.Path ?? string.Empty;
                if (!IsValidAssetPath(path))
                    return KbPackageReadResult.Fail(
                        $"Asset path '{Truncate(path)}' is invalid. Use 'assets/<name>' with letters, digits, '.', '-' or '_'.");
                if (!seenPaths.Add(path))
                    return KbPackageReadResult.Fail($"Asset '{path}' is listed twice in the manifest.");
                if (!entries.TryGetValue(path, out var assetEntry))
                    return KbPackageReadResult.Fail($"Asset '{path}' is listed in the manifest but missing from the package.");

                var filename = string.IsNullOrWhiteSpace(asset.Filename)
                    ? path["assets/".Length..]
                    : asset.Filename!;

                SniffedBlobWrite stored;
                try
                {
                    await using var raw = assetEntry.Open();
                    await using var limited = new CappedReadStream(raw, limits.MaxAssetBytes, budget, path);
                    // HTML is refused exactly like a direct upload: served back
                    // it is XSS bait even with the inline guard.
                    stored = await SniffedBlobWriter.WriteAsync(blobs, limited, null, filename,
                        mime => !string.Equals(mime, "text/html", StringComparison.OrdinalIgnoreCase), ct);
                }
                catch (BlobSourceRejectedException ex) { return KbPackageReadResult.Fail(ex.Message); }
                catch (InvalidDataException) { return KbPackageReadResult.Fail($"Asset '{path}' could not be decompressed."); }

                if (stored.Write is not { } write)
                    return KbPackageReadResult.Fail($"Asset '{path}' is an HTML file; HTML attachments are not allowed.");

                assets.Add(new KbPackageAsset(path, filename, stored.MimeType, write));
            }

            return KbPackageReadResult.Success(new KbPackageContent(title, manifest.Article.LocaleCode, bodyHtml, assets));
        }
    }

    /// Replaces <c>src</c>/<c>href</c> values that name a package asset
    /// (<c>assets/x.png</c> or <c>./assets/x.png</c>) by the URL in
    /// <paramref name="urlByPath"/>. Returns the asset references the body
    /// makes that the package does not carry, so the caller can warn.
    public static (string Html, IReadOnlyList<string> Unresolved) RewriteForImport(
        string bodyHtml, IReadOnlyDictionary<string, string> urlByPath)
    {
        var unresolved = new List<string>();
        var html = AssetRefPattern().Replace(bodyHtml ?? string.Empty, m =>
        {
            var path = m.Groups["path"].Value;
            if (urlByPath.TryGetValue(path, out var url))
                return $"{m.Groups["attr"].Value}={m.Groups["q"].Value}{url}{m.Groups["q"].Value}";
            if (!unresolved.Contains(path)) unresolved.Add(path);
            return m.Value;
        });
        return (html, unresolved);
    }

    public static bool IsValidAssetPath(string path) =>
        path.Length <= 128 && AssetPathPattern().IsMatch(path);

    // ───────────────────────────── Helpers ─────────────────────────────

    private static string UniqueAssetName(int index, string filename, HashSet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(filename ?? string.Empty);
        var ext = Path.GetExtension(filename ?? string.Empty);
        var safeStem = UnsafeNameChars().Replace(stem, "-").Trim('-', '.');
        var safeExt = UnsafeNameChars().Replace(ext, "");
        if (safeExt.Length > 10) safeExt = safeExt[..10];
        if (safeStem.Length > 60) safeStem = safeStem[..60];
        if (safeStem.Length == 0) safeStem = "file";
        var name = $"{index:00}-{safeStem}{safeExt}";
        while (!used.Add(name)) name = $"{index:00}-{safeStem}-{used.Count}{safeExt}";
        return name;
    }

    private static async Task WriteTextEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        await stream.WriteAsync(bytes, ct);
    }

    private static async Task<string> ReadTextAsync(
        ZipArchiveEntry entry, long maxBytes, DecompressionBudget budget, CancellationToken ct)
    {
        await using var raw = entry.Open();
        await using var limited = new CappedReadStream(raw, maxBytes, budget, entry.FullName);
        using var buffer = new MemoryStream();
        await limited.CopyToAsync(buffer, ct);
        return new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length)
            .TrimStart('\uFEFF');
    }

    private static string Truncate(string s) => s.Length > 60 ? s[..60] + "…" : s;

    // Lowercase GUIDs as the API writes them; matched case-insensitively.
    [GeneratedRegex("/api/kb/articles/[0-9a-fA-F-]{36}/attachments/(?<aid>[0-9a-fA-F-]{36})(\\?[^\"'\\s<>]*)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentUrlPattern();

    [GeneratedRegex("(?<attr>\\b(?:src|href))\\s*=\\s*(?<q>[\"'])(?:\\./)?(?<path>assets/[A-Za-z0-9._-]+)\\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetRefPattern();

    // No "..": a dot may not follow a dot, and the leaf may not start with one.
    [GeneratedRegex("^assets/[A-Za-z0-9_-](?:[A-Za-z0-9_-]|\\.(?!\\.))*$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPathPattern();

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeNameChars();

    // A BlobSourceRejectedException so the blob store discards the temp file
    // without counting the abort as a storage health failure.
    private sealed class PackageLimitException : BlobSourceRejectedException
    {
        public PackageLimitException(string message) : base(message) { }
    }

    private sealed class DecompressionBudget
    {
        private readonly long _max;
        private long _used;
        public DecompressionBudget(long max) => _max = max;

        public void Consume(int count)
        {
            _used += count;
            if (_used > _max)
                throw new PackageLimitException(
                    $"The package expands to more than {_max / 1_048_576} MB; it was rejected.");
        }
    }

    /// Counts the bytes actually decompressed — the sizes in the zip's own
    /// headers are attacker-controlled and never trusted.
    private sealed class CappedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private readonly DecompressionBudget _budget;
        private readonly string _name;
        private long _read;

        public CappedReadStream(Stream inner, long max, DecompressionBudget budget, string name)
        {
            _inner = inner; _max = max; _budget = budget; _name = name;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Track(_inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Track(await _inner.ReadAsync(buffer, ct));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Track(int n)
        {
            _read += n;
            if (_read > _max)
                throw new PackageLimitException($"'{_name}' is larger than the {FormatSize(_max)} limit.");
            _budget.Consume(n);
            return n;
        }

        private static string FormatSize(long bytes) =>
            bytes >= 1_048_576 ? $"{bytes / 1_048_576} MB" : $"{Math.Max(1, bytes / 1024)} KB";

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

public sealed record KbPackageLimits(int MaxEntries, long MaxAssetBytes, long MaxTotalUncompressedBytes);

public sealed record KbPackageExport(
    Guid ArticleId,
    string Title,
    string LocaleCode,
    string BodyHtml,
    DateTime ExportedUtc,
    IReadOnlyList<KbPackageExportAttachment> Attachments);

public sealed record KbPackageExportAttachment(Guid Id, string ContentHash, string Filename);

public sealed record KbPackageAsset(string Path, string Filename, string MimeType, BlobWriteResult Write);

public sealed record KbPackageContent(
    string Title, string? LocaleCode, string BodyHtml, IReadOnlyList<KbPackageAsset> Assets);

public sealed record KbPackageReadResult(string? Error, KbPackageContent? Content)
{
    public static KbPackageReadResult Fail(string error) => new(error, null);
    public static KbPackageReadResult Success(KbPackageContent content) => new(null, content);
}

public sealed record KbPackageManifest(
    string? Format,
    int Version,
    DateTime? ExportedUtc,
    KbPackageManifestArticle? Article,
    IReadOnlyList<KbPackageManifestAsset>? Assets);

public sealed record KbPackageManifestArticle(string? Title, string? LocaleCode, string? Body);

public sealed record KbPackageManifestAsset(string? Path, string? Filename);
