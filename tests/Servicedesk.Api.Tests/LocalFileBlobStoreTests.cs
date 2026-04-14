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
        _store = new LocalFileBlobStore(new StubSettings(_root));
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
