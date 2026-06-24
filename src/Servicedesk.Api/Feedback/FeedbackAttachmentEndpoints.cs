using System.Buffers;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Feedback;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Feedback;

/// Inline-image upload + download for Employee Feedback entries. Reuses the
/// existing blob pipeline (content-addressed dedup, MIME-sniff, size cap, HTML
/// rejection) from the ticket/KB endpoints; only the ownership
/// (<c>owner_kind='FeedbackEntry'</c>) and authorization (effective feedback
/// access — restricted users are scoped to their own entries) differ. Mirrors
/// <see cref="Servicedesk.Api.KnowledgeBase.KbAttachmentEndpoints"/>.
public static class FeedbackAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feedback/entries")
            .WithTags("Feedback")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // POST /api/feedback/entries/{id}/attachments — multipart upload.
        group.MapPost("/{id:guid}/attachments", async (
            Guid id, HttpContext http,
            IUserService users, IFeedbackEntryService entries,
            IAttachmentRepository attachments, IBlobStore blobs,
            ISettingsService settings, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;

            var entry = await entries.GetAsync(id, ct);
            if (entry is null) return Results.NotFound();
            // Restricted users may only attach to their own entries.
            if (access == FeedbackAccess.OwnOnly && entry.CreatedByUserId != userId)
                return Results.NotFound();

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
            var attachmentId = await attachments.CreateForFeedbackEntryAsync(new NewFeedbackEntryAttachment(
                EntryId: id,
                ContentHash: writeResult.ContentHash,
                SizeBytes: writeResult.SizeBytes,
                MimeType: sniffedMime,
                OriginalFilename: safeFilename), ct);

            await FeedbackAudit.WriteAsync(audit, http, "feedback.entry.attachment.added", attachmentId.ToString(),
                new { entryId = id, filename = safeFilename, mimeType = sniffedMime, size = writeResult.SizeBytes });

            return Results.Created($"/api/feedback/entries/{id}/attachments/{attachmentId}", new
            {
                id = attachmentId,
                url = $"/api/feedback/entries/{id}/attachments/{attachmentId}",
                mimeType = sniffedMime,
                size = writeResult.SizeBytes,
                filename = safeFilename,
            });
        }).WithName("UploadFeedbackAttachment").WithOpenApi()
          .DisableRequestTimeout();

        // GET /api/feedback/entries/{id}/attachments/{attachmentId} — download.
        group.MapGet("/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, bool? inline, HttpContext http,
            IUserService users, IFeedbackEntryService entries,
            IAttachmentRepository attachments, IBlobStore blobs,
            CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;

            var entry = await entries.GetAsync(id, ct);
            if (entry is null) return Results.NotFound();
            // Restricted users may only read attachments on their own entries.
            if (access == FeedbackAccess.OwnOnly && entry.CreatedByUserId != userId)
                return Results.NotFound();

            var att = await attachments.GetByIdAsync(attachmentId, ct);
            if (att is null) return Results.NotFound();
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash))
                return Results.NotFound();
            if (att.OwnerKind != "FeedbackEntry" || att.OwnerId != id)
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
        }).WithName("GetFeedbackAttachment").WithOpenApi();

        return app;
    }

    private static async Task<(Guid UserId, FeedbackAccess Access, IResult? Fail)> GateAsync(
        HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        if (userId == Guid.Empty) return (Guid.Empty, FeedbackAccess.None, Results.Unauthorized());
        var access = (await users.GetFeedbackAccessAsync(userId, ct)).Access;
        if (access == FeedbackAccess.None) return (Guid.Empty, FeedbackAccess.None, Results.Forbid());
        return (userId, access, null);
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
