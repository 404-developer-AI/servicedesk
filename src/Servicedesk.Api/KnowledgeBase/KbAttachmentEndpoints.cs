using System.Buffers;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.KnowledgeBase;

/// User-upload + download for KB-article attachments. The blob pipeline
/// (content-addressed dedup, MIME-sniff, size cap) is the existing one
/// from <see cref="Servicedesk.Api.Tickets.TicketAttachmentEndpoints"/>;
/// this surface only differs in ownership (`owner_kind='KbArticle'`) and
/// authorisation (Agent + Admin, no queue scoping).
public static class KbAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapKbAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/kb")
            .WithTags("KnowledgeBase")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // POST /api/kb/articles/{id}/attachments — multipart upload.
        // Returns the same shape as the ticket endpoint so the shared
        // RichTextEditor can route paste/drag/drop/paperclip flows through
        // a single TicketAttachmentMeta-typed callback regardless of context.
        group.MapPost("/articles/{id:guid}/attachments", async (
            Guid id, HttpContext http,
            IKbArticleRepository articleRepo,
            IAttachmentRepository attachments, IBlobStore blobs,
            ISettingsService settings, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var article = await articleRepo.GetArticleAsync(id, ct);
            if (article is null) return Results.NotFound();

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

            // Sniff the first 512 bytes server-side and refuse HTML — even
            // an inline-served HTML "image" with a manipulated Content-Type
            // is an XSS vector.
            var headBuffer = ArrayPool<byte>.Shared.Rent(MimeSniffer.SniffWindowBytes);
            int headLen;
            string sniffedMime;
            BlobWriteResult writeResult;
            try
            {
                await using var source = file.OpenReadStream();
                headLen = await ReadFullyAsync(source, headBuffer.AsMemory(0, MimeSniffer.SniffWindowBytes), ct);
                sniffedMime = MimeSniffer.Sniff(headBuffer.AsSpan(0, headLen), file.ContentType, file.FileName);
                if (string.Equals(sniffedMime, "text/html", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "HTML uploads are not allowed." });

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
            var attachmentId = await attachments.CreateForKbArticleAsync(new NewKbArticleAttachment(
                ArticleId: id,
                ContentHash: writeResult.ContentHash,
                SizeBytes: writeResult.SizeBytes,
                MimeType: sniffedMime,
                OriginalFilename: safeFilename), ct);

            await KbAudit.WriteAsync(audit, http, "kb.article.attachment.added", attachmentId.ToString(),
                new { articleId = id, filename = safeFilename, mimeType = sniffedMime, size = writeResult.SizeBytes });

            return Results.Created($"/api/kb/articles/{id}/attachments/{attachmentId}", new
            {
                id = attachmentId,
                url = $"/api/kb/articles/{id}/attachments/{attachmentId}",
                mimeType = sniffedMime,
                size = writeResult.SizeBytes,
                filename = safeFilename,
            });
        }).WithName("UploadKbAttachment").WithOpenApi()
          .DisableRequestTimeout();

        // GET /api/kb/articles/{id}/attachments/{attachmentId} — download.
        // ETag from the content-hash makes repeated <img> renders cache-hits.
        // The article-existence + owner check refuses cross-article guesses
        // (an attachment uploaded to article A is not reachable via article B).
        group.MapGet("/articles/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, HttpContext http,
            bool? inline,
            IKbArticleRepository articleRepo,
            IAttachmentRepository attachments, IBlobStore blobs,
            CancellationToken ct) =>
        {
            var article = await articleRepo.GetArticleAsync(id, ct);
            if (article is null) return Results.NotFound();

            var att = await attachments.GetByIdAsync(attachmentId, ct);
            if (att is null) return Results.NotFound();
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash))
                return Results.NotFound();
            if (att.OwnerKind != "KbArticle" || att.OwnerId != id)
                return Results.NotFound();

            var etag = $"\"{att.ContentHash}\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, max-age=604800, must-revalidate";
            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag)))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var stream = await blobs.OpenReadAsync(att.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            var fileName = string.IsNullOrWhiteSpace(att.OriginalFilename) ? "attachment" : att.OriginalFilename;
            var contentType = string.IsNullOrWhiteSpace(att.MimeType) ? "application/octet-stream" : att.MimeType;
            return inline == true
                ? Results.File(stream, contentType, fileDownloadName: null, enableRangeProcessing: true)
                : Results.File(stream, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
        }).WithName("GetKbAttachment").WithOpenApi();

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
        if (string.IsNullOrWhiteSpace(input)) return "attachment";
        var leaf = Path.GetFileName(input);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = leaf.Select(c => invalid.Contains(c) || c == '<' || c == '>' || c == ':' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimStart('.');
        if (string.IsNullOrEmpty(s)) s = "attachment";
        return s.Length > 200 ? s[..200] : s;
    }

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
