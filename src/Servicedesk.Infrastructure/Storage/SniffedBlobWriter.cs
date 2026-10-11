using System.Buffers;

namespace Servicedesk.Infrastructure.Storage;

/// Outcome of <see cref="SniffedBlobWriter.WriteAsync"/>. <see cref="Write"/>
/// is null when the sniffed type was refused (nothing was stored).
public readonly record struct SniffedBlobWrite(string MimeType, BlobWriteResult? Write)
{
    public bool Rejected => Write is null;
}

/// The one "sniff, then store" path for every file that enters the blob
/// store from outside (uploads, imports): reads the first
/// <see cref="MimeSniffer.SniffWindowBytes"/>, decides the server-side MIME
/// type, lets the caller refuse it, then streams head + remainder into the
/// store in a single pass — never the whole file in memory.
public static class SniffedBlobWriter
{
    /// <param name="accept">Return false to refuse the sniffed MIME type (nothing is written).</param>
    public static async Task<SniffedBlobWrite> WriteAsync(
        IBlobStore blobs, Stream source, string? clientMime, string? fileName,
        Func<string, bool>? accept, CancellationToken ct)
    {
        var head = ArrayPool<byte>.Shared.Rent(MimeSniffer.SniffWindowBytes);
        try
        {
            var headLen = await ReadFullyAsync(source, head.AsMemory(0, MimeSniffer.SniffWindowBytes), ct);
            var mime = MimeSniffer.Sniff(head.AsSpan(0, headLen), clientMime, fileName);
            if (accept is not null && !accept(mime)) return new SniffedBlobWrite(mime, null);

            await using var combined = new HeadedReadStream(head.AsMemory(0, headLen), source);
            var write = await blobs.WriteAsync(combined, ct);
            return new SniffedBlobWrite(mime, write);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(head);
        }
    }

    /// Re-sniffs a blob that is already stored (paths that cannot sniff before
    /// writing). Returns <paramref name="fallbackMime"/> when the blob is gone.
    public static async Task<string> SniffStoredAsync(
        IBlobStore blobs, string contentHash, string? fallbackMime, string? fileName, CancellationToken ct)
    {
        await using var stream = await blobs.OpenReadAsync(contentHash, ct);
        if (stream is null) return fallbackMime ?? "application/octet-stream";
        var buffer = new byte[MimeSniffer.SniffWindowBytes];
        var read = await ReadFullyAsync(stream, buffer, ct);
        return MimeSniffer.Sniff(buffer.AsSpan(0, read), fallbackMime, fileName);
    }

    private static async Task<int> ReadFullyAsync(Stream source, Memory<byte> dest, CancellationToken ct)
    {
        var total = 0;
        while (total < dest.Length)
        {
            var read = await source.ReadAsync(dest[total..], ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// An in-memory head buffer followed by the remainder of the source
    /// stream, read-only. Does not own (dispose) the source.
    private sealed class HeadedReadStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _head;
        private readonly Stream _tail;
        private int _headOffset;

        public HeadedReadStream(ReadOnlyMemory<byte> head, Stream tail)
        {
            _head = head;
            _tail = tail;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_headOffset < _head.Length)
            {
                var take = Math.Min(buffer.Length, _head.Length - _headOffset);
                _head.Span.Slice(_headOffset, take).CopyTo(buffer);
                _headOffset += take;
                return take;
            }
            return _tail.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_headOffset < _head.Length)
            {
                var take = Math.Min(buffer.Length, _head.Length - _headOffset);
                _head.Slice(_headOffset, take).CopyTo(buffer);
                _headOffset += take;
                return ValueTask.FromResult(take);
            }
            return _tail.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
