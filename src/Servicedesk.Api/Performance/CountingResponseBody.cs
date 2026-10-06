using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace Servicedesk.Api.Performance;

/// Counts the bytes a response body writes, whichever API the handler uses
/// (Stream, PipeWriter or SendFile). Minimal-API JSON responses carry no
/// Content-Length (chunked), so this is the only way to see payload size —
/// large JSON payloads are a classic hidden source of slowness.
/// Pure pass-through otherwise: every call delegates to the original feature.
public sealed class CountingResponseBody : IHttpResponseBodyFeature
{
    private readonly IHttpResponseBodyFeature _inner;
    private CountingStream? _stream;
    private CountingPipeWriter? _writer;
    private long _bytes;

    private CountingResponseBody(IHttpResponseBodyFeature inner) => _inner = inner;

    public long BytesWritten => Interlocked.Read(ref _bytes);

    public static CountingResponseBody? Install(HttpContext context)
    {
        var inner = context.Features.Get<IHttpResponseBodyFeature>();
        if (inner is null) return null;
        var counting = new CountingResponseBody(inner);
        context.Features.Set<IHttpResponseBodyFeature>(counting);
        return counting;
    }

    internal void Add(long count)
    {
        if (count > 0) Interlocked.Add(ref _bytes, count);
    }

    public Stream Stream => _stream ??= new CountingStream(_inner.Stream, this);

    public PipeWriter Writer => _writer ??= new CountingPipeWriter(_inner.Writer, this);

    public void DisableBuffering() => _inner.DisableBuffering();

    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        await _inner.SendFileAsync(path, offset, count, cancellationToken);
        try
        {
            Add(count ?? Math.Max(0, new FileInfo(path).Length - offset));
        }
        catch
        {
            // size is informational only
        }
    }

    public Task CompleteAsync() => _inner.CompleteAsync();

    private sealed class CountingPipeWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private readonly CountingResponseBody _owner;

        public CountingPipeWriter(PipeWriter inner, CountingResponseBody owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override void Advance(int bytes)
        {
            _owner.Add(bytes);
            _inner.Advance(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => _inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => _inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);
        public override bool CanGetUnflushedBytes => _inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => _inner.UnflushedBytes;

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            _owner.Add(source.Length);
            return _inner.WriteAsync(source, cancellationToken);
        }
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly CountingResponseBody _owner;

        public CountingStream(Stream inner, CountingResponseBody owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _owner.Add(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _owner.Add(buffer.Length);
            _inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _owner.Add(count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _owner.Add(buffer.Length);
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            _owner.Add(1);
            _inner.WriteByte(value);
        }
    }
}
