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
///
/// GC safety: a dedup hit refreshes the existing file's mtime, and both that
/// touch/rename and the GC's <see cref="DeleteIfUntouchedSinceAsync"/> run
/// under the same per-hash lock. The GC only deletes files untouched for its
/// grace period, so a writer that just reused (or re-created) a blob always
/// keeps it. The locks are in-process: one app instance per install.
public sealed class LocalFileBlobStore : IBlobStore, IBlobStoreMaintenance
{
    private const string TempDirName = ".tmp";
    private static readonly SemaphoreSlim[] HashLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private const int BufferSize = 81920; // 80 KB — matches default Stream.CopyTo chunk
    private static readonly char[] HexChars = "0123456789abcdef".ToCharArray();

    private readonly ISettingsService _settings;
    private readonly IBlobStoreHealth _health;

    public LocalFileBlobStore(ISettingsService settings, IBlobStoreHealth health)
    {
        _settings = settings;
        _health = health;
    }

    public async Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        string root;
        string tmpDir;
        try
        {
            root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
            tmpDir = Path.Combine(root, TempDirName);
            Directory.CreateDirectory(tmpDir);
        }
        catch (Exception ex)
        {
            _health.RecordFailure("write.prepare", ex);
            throw;
        }

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

            var gate = LockFor(hash);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(finalPath))
                {
                    // Dedup: identical content already on disk. Drop the temp
                    // and refresh the mtime so the GC grace period restarts —
                    // the caller is about to write a row that references it.
                    File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
                    File.Delete(tmpPath);
                    _health.RecordSuccess();
                    return new BlobWriteResult(hash, size);
                }

                File.Move(tmpPath, finalPath);
            }
            finally
            {
                gate.Release();
            }

            _health.RecordSuccess();
            return new BlobWriteResult(hash, size);
        }
        catch (Exception ex)
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            }
            if (ex is not BlobSourceRejectedException) _health.RecordFailure("write", ex);
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

    public Task<string> GetRootPathAsync(CancellationToken cancellationToken = default) =>
        GetRootAsync(cancellationToken);

    public async IAsyncEnumerable<BlobFileInfo> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(root)) yield break;

        foreach (var shard1 in Directory.EnumerateDirectories(root))
        {
            var name1 = Path.GetFileName(shard1);
            if (name1.Length != 2 || !IsHex(name1)) continue; // skips .tmp and anything foreign
            foreach (var shard2 in Directory.EnumerateDirectories(shard1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name2 = Path.GetFileName(shard2);
                if (name2.Length != 2 || !IsHex(name2)) continue;
                foreach (var file in new DirectoryInfo(shard2).EnumerateFiles())
                {
                    var hash = file.Name;
                    if (!IsValidSha256Hex(hash) || !hash.StartsWith(name1 + name2, StringComparison.Ordinal)) continue;
                    yield return new BlobFileInfo(hash, file.Length, file.LastWriteTimeUtc);
                }
            }
        }
    }

    public async Task<long?> DeleteIfUntouchedSinceAsync(string contentHash, DateTime untouchedSinceUtc, CancellationToken cancellationToken = default)
    {
        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        var path = ResolvePath(root, contentHash);
        var gate = LockFor(contentHash);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LastWriteTimeUtc >= untouchedSinceUtc) return null;
            var size = info.Length;
            info.Delete();
            return size;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<(int Files, long Bytes)> PurgeStaleTempFilesAsync(DateTime olderThanUtc, bool delete, CancellationToken cancellationToken = default)
    {
        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        var tmpDir = Path.Combine(root, TempDirName);
        if (!Directory.Exists(tmpDir)) return (0, 0);

        var files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(tmpDir).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc >= olderThanUtc) continue;
            var size = file.Length;
            if (delete)
            {
                try { file.Delete(); }
                catch (IOException) { continue; } // still being written — next run
            }
            files++;
            bytes += size;
        }
        return (files, bytes);
    }

    private static SemaphoreSlim LockFor(string contentHash) =>
        HashLocks[Convert.ToInt32(contentHash.Substring(0, 2), 16)];

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
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
