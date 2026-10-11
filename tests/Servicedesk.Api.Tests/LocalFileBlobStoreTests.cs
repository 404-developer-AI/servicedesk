using System.Security.Cryptography;
using System.Text;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class LocalFileBlobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileBlobStore _store;

    public LocalFileBlobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sd-blob-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new LocalFileBlobStore(new StubSettings(_root), new BlobStoreHealth());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Write_then_read_round_trips_content()
    {
        var bytes = Encoding.UTF8.GetBytes("hello blob store");
        var result = await _store.WriteAsync(new MemoryStream(bytes));

        Assert.Equal(bytes.Length, result.SizeBytes);
        Assert.Equal(Sha256Hex(bytes), result.ContentHash);

        await using var read = await _store.OpenReadAsync(result.ContentHash);
        Assert.NotNull(read);
        using var ms = new MemoryStream();
        await read!.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task Writing_same_content_twice_dedups_to_one_file()
    {
        var bytes = Encoding.UTF8.GetBytes("dedup target");

        var first = await _store.WriteAsync(new MemoryStream(bytes));
        var second = await _store.WriteAsync(new MemoryStream(bytes));

        Assert.Equal(first.ContentHash, second.ContentHash);

        // Exactly one blob file on disk (plus the .tmp scratch directory).
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".tmp" + Path.DirectorySeparatorChar))
            .ToArray();
        Assert.Single(files);
    }

    [Fact]
    public async Task Write_leaves_no_temp_files_behind()
    {
        var bytes = Encoding.UTF8.GetBytes("no leak");
        await _store.WriteAsync(new MemoryStream(bytes));

        var tmpDir = Path.Combine(_root, ".tmp");
        if (Directory.Exists(tmpDir))
        {
            Assert.Empty(Directory.GetFiles(tmpDir));
        }
    }

    [Fact]
    public async Task Exists_returns_false_for_unknown_hash_and_true_after_write()
    {
        var bytes = Encoding.UTF8.GetBytes("exists?");
        var unknown = Sha256Hex(Encoding.UTF8.GetBytes("never written"));

        Assert.False(await _store.ExistsAsync(unknown));

        var result = await _store.WriteAsync(new MemoryStream(bytes));
        Assert.True(await _store.ExistsAsync(result.ContentHash));
    }

    [Fact]
    public async Task OpenRead_returns_null_when_blob_is_missing()
    {
        var unknown = Sha256Hex(Encoding.UTF8.GetBytes("ghost"));
        var stream = await _store.OpenReadAsync(unknown);
        Assert.Null(stream);
    }

    [Fact]
    public async Task Delete_reports_whether_the_file_was_removed()
    {
        var bytes = Encoding.UTF8.GetBytes("disposable");
        var result = await _store.WriteAsync(new MemoryStream(bytes));

        Assert.True(await _store.DeleteAsync(result.ContentHash));
        Assert.False(await _store.ExistsAsync(result.ContentHash));
        Assert.False(await _store.DeleteAsync(result.ContentHash));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("short")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")] // 64 chars, non-hex
    public async Task Malformed_hashes_are_rejected(string badHash)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(badHash));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ExistsAsync(badHash));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(badHash));
    }

    private static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// Minimal ISettingsService that only answers Storage.BlobRoot — that's
    /// the only key LocalFileBlobStore touches.
    [Fact]
    public async Task Enumerate_lists_blob_files_and_skips_tmp_and_foreign_files()
    {
        var a = await _store.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes("a")));
        var b = await _store.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes("bb")));
        Directory.CreateDirectory(Path.Combine(_root, ".tmp"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".tmp", "partial"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "README"), "not a blob");

        var listed = new List<BlobFileInfo>();
        await foreach (var f in _store.EnumerateAsync()) listed.Add(f);

        Assert.Equal(new[] { a.ContentHash, b.ContentHash }.OrderBy(h => h), listed.Select(f => f.ContentHash).OrderBy(h => h));
        Assert.Equal(2, listed.Single(f => f.ContentHash == b.ContentHash).SizeBytes);
    }

    [Fact]
    public async Task DeleteIfUntouchedSince_only_deletes_files_older_than_the_cutoff()
    {
        var w = await _store.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes("gc me")));

        // Fresh file: inside the grace window, kept.
        Assert.Null(await _store.DeleteIfUntouchedSinceAsync(w.ContentHash, DateTime.UtcNow.AddHours(-1)));
        Assert.True(await _store.ExistsAsync(w.ContentHash));

        Assert.Equal(w.SizeBytes, await _store.DeleteIfUntouchedSinceAsync(w.ContentHash, DateTime.UtcNow.AddMinutes(1)));
        Assert.False(await _store.ExistsAsync(w.ContentHash));
    }

    [Fact]
    public async Task Dedup_write_refreshes_mtime_so_the_gc_keeps_a_reused_blob()
    {
        var bytes = Encoding.UTF8.GetBytes("reused");
        var first = await _store.WriteAsync(new MemoryStream(bytes));
        var path = Directory.GetFiles(_root, first.ContentHash, SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));

        await _store.WriteAsync(new MemoryStream(bytes));

        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(await _store.DeleteIfUntouchedSinceAsync(first.ContentHash, DateTime.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task PurgeStaleTempFiles_counts_in_dry_run_and_deletes_otherwise()
    {
        var tmp = Path.Combine(_root, ".tmp");
        Directory.CreateDirectory(tmp);
        var stale = Path.Combine(tmp, "stale");
        var fresh = Path.Combine(tmp, "fresh");
        await File.WriteAllTextAsync(stale, "12345");
        await File.WriteAllTextAsync(fresh, "1");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        var dry = await _store.PurgeStaleTempFilesAsync(DateTime.UtcNow.AddDays(-1), delete: false);
        Assert.Equal((1, 5L), dry);
        Assert.True(File.Exists(stale));

        var real = await _store.PurgeStaleTempFilesAsync(DateTime.UtcNow.AddDays(-1), delete: true);
        Assert.Equal((1, 5L), real);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Rejected_source_aborts_the_write_without_a_health_failure()
    {
        var health = new BlobStoreHealth();
        var store = new LocalFileBlobStore(new StubSettings(_root), health);

        await Assert.ThrowsAsync<BlobSourceRejectedException>(() => store.WriteAsync(new RejectingStream()));

        Assert.Equal(0, health.Snapshot().ConsecutiveFailures);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".tmp")));
    }

    private sealed class RejectingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new BlobSourceRejectedException("too large");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubSettings : ISettingsService
    {
        private readonly string _root;
        public StubSettings(string root) => _root = root;

        public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            if (key == SettingKeys.Storage.BlobRoot && typeof(T) == typeof(string))
            {
                return Task.FromResult((T)(object)_root);
            }
            throw new NotSupportedException($"StubSettings does not handle '{key}'.");
        }

        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
