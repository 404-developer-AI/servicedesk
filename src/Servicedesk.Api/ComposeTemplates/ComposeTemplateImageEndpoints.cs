using System.Buffers;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.ComposeTemplates;

/// Inline-image upload + download for mail templates (v0.0.92). Reuses the
/// existing blob pipeline (content-addressed dedup, MIME-sniff, size cap)
/// from the ticket/KB/feedback endpoints. Unlike those, uploads here are
/// restricted to images — a template embeds pictures, never file
/// attachments — and the rows are self-owned
/// (<c>owner_kind='ComposeTemplateImage'</c>) because a template can be
/// edited before it is first saved, so there is no parent id to point at.
///
/// Upload is Admin (templates are admin-managed); download is Agent because
/// every agent renders template bodies in the compose editor and the ticket
/// timeline. At send time <c>OutboundMailService</c> copies each referenced
/// image onto the ticket and cid-embeds it, so mail recipients never need
/// this endpoint.
public static class ComposeTemplateImageEndpoints
{
    public static IEndpointRouteBuilder MapComposeTemplateImageEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/settings/compose-templates/images — multipart upload,
        // admin-only, same auth posture as the template CRUD it belongs to.
        app.MapPost("/api/settings/compose-templates/images", async (
            HttpContext http,
            IAttachmentRepository attachments, IBlobStore blobs,
            ISettingsService settings, IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required." });

            const long HardBodyCeilingBytes = 52_428_800;
            if (http.Request.ContentLength is long advertised && advertised > HardBodyCeilingBytes)
                return Results.Json(new { error = "Request body exceeds 50 MB hard ceiling." }, statusCode: 413);

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Upload a non-empty file in the 'file' field." });

            var maxBytes = await settings.GetAsync<long>(SettingKeys.Storage.MaxAttachmentBytes, ct);
            if (maxBytes <= 0) maxBytes = 26_214_400; // 25 MB safety net
            if (file.Length > maxBytes)
                return Results.Json(new
                {
                    error = $"File exceeds the {Math.Max(1, maxBytes / 1_048_576)} MB limit (Storage.MaxAttachmentBytes).",
                }, statusCode: 413);

            var headBuffer = ArrayPool<byte>.Shared.Rent(MimeSniffer.SniffWindowBytes);
            int headLen;
            string sniffedMime;
            BlobWriteResult writeResult;
            try
            {
                await using var source = file.OpenReadStream();
                headLen = await ReadFullyAsync(source, headBuffer.AsMemory(0, MimeSniffer.SniffWindowBytes), ct);
                sniffedMime = MimeSniffer.Sniff(headBuffer.AsSpan(0, headLen), file.ContentType, file.FileName);
                // Templates embed pictures only. The sniffed type (not the
                // client-supplied one) decides, so a renamed .exe or an HTML
                // payload can't slip through as "image/png".
                if (!sniffedMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Only images can be embedded in a template." });

                using var combined = new ConcatStream(headBuffer.AsMemory(0, headLen), source);
                writeResult = await blobs.WriteAsync(combined, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headBuffer);
            }

            if (writeResult.SizeBytes > maxBytes)
                return Results.Json(new
                {
                    error = $"File exceeds the {Math.Max(1, maxBytes / 1_048_576)} MB limit (Storage.MaxAttachmentBytes).",
                }, statusCode: 413);

            var safeFilename = SanitizeFilename(file.FileName);
            var imageId = await attachments.CreateForComposeTemplateImageAsync(new NewComposeTemplateImage(
                ContentHash: writeResult.ContentHash,
                SizeBytes: writeResult.SizeBytes,
                MimeType: sniffedMime,
                OriginalFilename: safeFilename), ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "compose_template.image.uploaded",
                Actor: actor,
                ActorRole: role,
                Target: imageId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { filename = safeFilename, mimeType = sniffedMime, size = writeResult.SizeBytes }));

            return Results.Created($"/api/compose-templates/images/{imageId}", new
            {
                id = imageId,
                url = $"/api/compose-templates/images/{imageId}",
                mimeType = sniffedMime,
                size = writeResult.SizeBytes,
                filename = safeFilename,
            });
        }).WithTags("ComposeTemplates")
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
          .WithName("UploadComposeTemplateImage").WithOpenApi()
          .DisableRequestTimeout();

        // GET /api/compose-templates/images/{id} — agent-readable download.
        // The template body's <img> tags point here; the compose editor, the
        // note/mail timeline and the admin editor all render through it.
        app.MapGet("/api/compose-templates/images/{id:guid}", async (
            Guid id, bool? inline, HttpContext http,
            IAttachmentRepository attachments, IBlobStore blobs,
            CancellationToken ct) =>
        {
            var att = await attachments.GetByIdAsync(id, ct);
            if (att is null) return Results.NotFound();
            if (att.OwnerKind != "ComposeTemplateImage") return Results.NotFound();
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash))
                return Results.NotFound();

            var etag = $"\"{att.ContentHash}\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, max-age=604800, must-revalidate";
            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag)))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var stream = await blobs.OpenReadAsync(att.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            // Inline only for inline-safe types (audit v0.1.1 #2).
            var fileName = string.IsNullOrWhiteSpace(att.OriginalFilename) ? "image" : att.OriginalFilename;
            return Servicedesk.Api.Tickets.AttachmentResponse.File(
                stream, att.MimeType, fileName, inline == true);
        }).WithTags("ComposeTemplates")
          .RequireAuthorization(AuthorizationPolicies.RequireAgent)
          .WithName("GetComposeTemplateImage").WithOpenApi();

        return app;
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

    private static string SanitizeFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "image";
        var leaf = Path.GetFileName(input);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = leaf.Select(c => invalid.Contains(c) || c == '<' || c == '>' || c == ':' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimStart('.');
        if (string.IsNullOrEmpty(s)) s = "image";
        return s.Length > 200 ? s[..200] : s;
    }

    /// Concatenates an in-memory head buffer with the remainder of the source
    /// stream so blob.WriteAsync sees the full payload exactly once. Read-only.
    private sealed class ConcatStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _head;
        private readonly Stream _tail;
        private int _headOffset;

        public ConcatStream(ReadOnlyMemory<byte> head, Stream tail)
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_headOffset < _head.Length)
            {
                var take = Math.Min(count, _head.Length - _headOffset);
                _head.Span.Slice(_headOffset, take).CopyTo(buffer.AsSpan(offset, take));
                _headOffset += take;
                return take;
            }
            return _tail.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_headOffset < _head.Length)
            {
                var take = Math.Min(buffer.Length, _head.Length - _headOffset);
                _head.Slice(_headOffset, take).CopyTo(buffer);
                _headOffset += take;
                return take;
            }
            return await _tail.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
