using System.Text.Json;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Security;

public static class CspReportEndpoint
{
    public static IEndpointRouteBuilder MapCspReportEndpoint(this IEndpointRouteBuilder app)
    {
        // 16 KB is an order of magnitude above a legitimate CSP report (the
        // longest field is usually the document-uri). Anything bigger is
        // either a misbehaving browser or an abuse attempt trying to inflate
        // the audit table.
        const int MaxBodyBytes = 16 * 1024;

        app.MapPost("/api/security/csp-report", async (HttpContext ctx, IAuditLogger audit, CancellationToken ct) =>
        {
            object? report = null;
            try
            {
                using var limited = new LimitedStream(ctx.Request.Body, MaxBodyBytes);
                using var doc = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
                report = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
            }
            catch
            {
                report = new { malformed = true };
            }

            await audit.LogAsync(new AuditEvent(
                EventType: "csp_violation",
                Actor: "browser",
                ActorRole: "anon",
                ClientIp: ctx.Connection.RemoteIpAddress?.ToString(),
                UserAgent: ctx.Request.Headers.UserAgent.ToString(),
                Payload: report), ct);

            return Results.NoContent();
        })
        .WithName("CspReport")
        .WithOpenApi()
        .RequireRateLimiting("csp-report");

        return app;
    }

    /// Read-only stream wrapper that hard-caps the number of bytes a caller
    /// can read from the underlying stream. Reading past the cap throws so
    /// the JSON parser bails out and the endpoint records {malformed:true}.
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private long _read;

        public LimitedStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = (int)Math.Min(count, Math.Max(0, _max - _read + 1));
            if (allowed <= 0) throw new InvalidOperationException("CSP report exceeds size limit.");
            var read = _inner.Read(buffer, offset, allowed);
            _read += read;
            if (_read > _max) throw new InvalidOperationException("CSP report exceeds size limit.");
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var allowed = (int)Math.Min(buffer.Length, Math.Max(0, _max - _read + 1));
            if (allowed <= 0) throw new InvalidOperationException("CSP report exceeds size limit.");
            var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
            _read += read;
            if (_read > _max) throw new InvalidOperationException("CSP report exceeds size limit.");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
