using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class BlobGcTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Hash256_round_trips_equal_hashes_into_one_set_entry()
    {
        Assert.True(BlobGarbageCollector.Hash256.TryParse(Hash, out var a));
        Assert.True(BlobGarbageCollector.Hash256.TryParse(new string(Hash.ToCharArray()), out var b));
        Assert.Single(new HashSet<BlobGarbageCollector.Hash256> { a, b });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")] // uppercase never written
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Hash256_rejects_anything_that_is_not_a_lowercase_sha256(string? value) =>
        Assert.False(BlobGarbageCollector.Hash256.TryParse(value, out _));

    [Theory]
    [InlineData(50.0, BlobDiskLevel.Ok)]
    [InlineData(80.0, BlobDiskLevel.Warning)]
    [InlineData(91.9, BlobDiskLevel.Warning)]
    [InlineData(92.0, BlobDiskLevel.Critical)]
    public void Disk_level_follows_the_thresholds(double used, BlobDiskLevel expected) =>
        Assert.Equal(expected, BlobDiskMonitor.Classify(used, warnPercent: 80, criticalPercent: 92));

    [Fact]
    public void Every_blob_referencing_column_is_in_both_gc_queries()
    {
        // A blob-owning column missing from either query gets its files
        // deleted by the GC — keep the mark and re-check sets identical.
        foreach (var column in new[]
        {
            "attachments", "raw_eml_blob_hash", "body_html_blob_hash",
            "signature_assets", "photo_blob_hash", "settings",
        })
        {
            Assert.Contains(column, BlobGarbageCollector.ReferencedSql);
            Assert.Contains(column, BlobGarbageCollector.StillReferencedSql);
        }
    }

    [Fact]
    public async Task MaxLengthReadStream_throws_once_the_cap_is_exceeded()
    {
        await using var ok = new MaxLengthReadStream(new MemoryStream(new byte[10]), 10);
        await ok.CopyToAsync(Stream.Null);

        await using var tooBig = new MaxLengthReadStream(new MemoryStream(new byte[11]), 10);
        await Assert.ThrowsAsync<InboundAttachmentTooLargeException>(() => tooBig.CopyToAsync(Stream.Null));
    }
}
