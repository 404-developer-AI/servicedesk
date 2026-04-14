using System.Security.Cryptography;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Storage;

/// Local filesystem implementation of <see cref="IBlobStore"/>. Blobs live
/// under <c>Storage.BlobRoot</c> in a two-level sharded path — for hash
/// <c>abcdef…</c> the file path is <c>&lt;root&gt;/ab/cd/abcdef…</c>. That
/// keeps any single directory under ~65k entries even at millions of blobs.
///
/// Writes are atomic: bytes land in <c>&lt;root&gt;/.tmp/&lt;guid&gt;</c>
/// first, are hashed during streaming, and are then renamed into place.
/// A concurrent write of the same content resolves into a single file via
/// the filesystem rename — duplicate temp files are discarded.
public sealed class LocalFileBlobStore : IBlobStore
{
    private const int BufferSize = 81920; // 80 KB — matches default Stream.CopyTo chunk
    private static readonly char[] HexChars = "0123456789abcdef".ToCharArray();

    private readonly ISettingsService _settings;

    public LocalFileBlobStore(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        var tmpDir = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(tmpDir);

        var tmpPath = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        long size;
        string hash;

        try
        {
            await using (var tmpStream = new FileStream(
                tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                long total = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    hasher.AppendData(buffer, 0, read);
                    await tmpStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                }

                await tmpStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                size = total;
                hash = ToHex(hasher.GetHashAndReset());
            }

            var finalPath = ResolvePath(root, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (File.Exists(finalPath))
            {
                // Dedup: identical content already on disk. Drop the temp.
                File.Delete(tmpPath);
                return new BlobWriteResult(hash, size);
            }

            try
            {
                File.Move(tmpPath, finalPath);
            }
            catch (IOException)
            {
                // Another writer won the race. Accept their file.
                if (File.Exists(finalPath))
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                    return new BlobWriteResult(hash, size);
                }
                throw;
            }

            return new BlobWriteResult(hash, size);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            }
            throw;
        }
    }

    public async Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        var path = ResolvePath(root, contentHash);
        if (!File.Exists(path)) return null;
        return new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task<bool> ExistsAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        return File.Exists(ResolvePath(root, contentHash));
    }

    public async Task<bool> DeleteAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        var path = ResolvePath(root, contentHash);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private async Task<string> GetRootAsync(CancellationToken cancellationToken)
    {
        var root = await _settings.GetAsync<string>(SettingKeys.Storage.BlobRoot, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"Setting '{SettingKeys.Storage.BlobRoot}' is empty. Configure a filesystem path for blob storage.");
        }
        return Path.GetFullPath(root);
    }

    private static string ResolvePath(string root, string contentHash)
    {
        if (!IsValidSha256Hex(contentHash))
        {
            throw new ArgumentException(
                "Content hash must be a 64-character lowercase SHA-256 hex string.", nameof(contentHash));
        }
        var shard1 = contentHash.Substring(0, 2);
        var shard2 = contentHash.Substring(2, 2);
        var full = Path.GetFullPath(Path.Combine(root, shard1, shard2, contentHash));
        // Defence-in-depth: confirm the resolved path is still under root.
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new ArgumentException("Resolved blob path escapes the configured blob root.", nameof(contentHash));
        }
        return full;
    }

    private static bool IsValidSha256Hex(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
        foreach (var c in value)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return true;
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = HexChars[bytes[i] >> 4];
            chars[i * 2 + 1] = HexChars[bytes[i] & 0x0f];
        }
        return new string(chars);
    }
}
