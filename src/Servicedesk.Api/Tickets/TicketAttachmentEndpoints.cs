using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Tickets;

/// User-upload endpoint and download endpoint for attachments that don't
/// originate from inbound mail — i.e. files an agent attaches to an internal
/// note, a public reply, or an outbound mail. Inbound-mail downloads continue
/// to flow through <see cref="TicketMailEndpoints"/> because those carry
/// per-mail audit metadata. Both surfaces are Agent+Admin only, queue-scoped.
public static class TicketAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapTicketAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // POST /api/tickets/{id}/attachments — multipart upload. Streams the
        // file body into IBlobStore one buffer at a time so a 25 MB upload
        // never holds the whole payload in memory. Size is enforced *during*
        // streaming (not via Content-Length, which a client controls); MIME
        // is sniffed from the first 512 bytes server-side and overrides the
        // client-supplied Content-Type when they disagree.
        group.MapPost("/{id:guid}/attachments", async (
            Guid id, HttpContext http,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IAttachmentRepository attachments, IBlobStore blobs,
            ISettingsService settings, IAuditLogger audit,
            CancellationToken ct) =>
        {
            // Queue-access first — return 404 to avoid leaking ticket existence.
            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required." });

            // Cheap pre-flight cap: if the client truthfully advertises a
            // gigantic Content-Length we reject before ReadFormAsync allocates
            // anything. Matches the nginx client_max_body_size = 50 MB. The
            // fine-grained, admin-tunable Storage.MaxAttachmentBytes still
            // applies after parsing — this is just a denial-of-service guard.
            const long HardBodyCeilingBytes = 52_428_800;
            if (http.Request.ContentLength is long advertised && advertised > HardBodyCeilingBytes)
            {
                return Results.Json(new { error = "Request body exceeds 50 MB hard ceiling." }, statusCode: 413);
            }

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Upload a non-empty file in the 'file' field." });

            var maxBytes = await settings.GetAsync<long>(SettingKeys.Storage.MaxAttachmentBytes, ct);
            if (maxBytes <= 0) maxBytes = 26_214_400; // 25 MB safety net
            if (file.Length > maxBytes)
            {
                return Results.Json(new
                {
                    error = $"File exceeds the {Math.Max(1, maxBytes / 1_048_576)} MB limit (Storage.MaxAttachmentBytes).",
                }, statusCode: 413);
            }

            // Sniff first — read the head into memory, then concat with the
            // rest of the stream when writing to the blob store. Reject HTML
            // outright; an HTML "attachment" served back inline is XSS bait
            // even with our content-type discipline.
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
                {
                    return Results.BadRequest(new { error = "HTML uploads are not allowed." });
                }

                // Re-stream: the head bytes we already read + the remainder.
                using var combined = new ConcatStream(headBuffer.AsMemory(0, headLen), source);
                writeResult = await blobs.WriteAsync(combined, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headBuffer);
            }

            if (writeResult.SizeBytes > maxBytes)
            {
                // We've written the blob; leave it on disk (it's content-
                // addressed and de-dups), but reject the row insert. The
                // orphan-sweeper will reclaim the bytes if no other row
                // references them.
                return Results.Json(new
                {
                    error = $"File exceeds the {Math.Max(1, maxBytes / 1_048_576)} MB limit (Storage.MaxAttachmentBytes).",
                }, statusCode: 413);
            }

            var safeFilename = SanitizeFilename(file.FileName);
            var attachmentId = await attachments.CreateUploadedAsync(new NewUploadedAttachment(
                TicketId: id,
                ContentHash: writeResult.ContentHash,
                SizeBytes: writeResult.SizeBytes,
                MimeType: sniffedMime,
                OriginalFilename: safeFilename), ct);

            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.attachment.uploaded",
                Actor: http.User.Identity?.Name ?? userId.ToString(),
                ActorRole: role,
                Target: attachmentId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    ticketId = id,
                    filename = safeFilename,
                    mimeType = sniffedMime,
                    size = writeResult.SizeBytes,
                }), ct);

            return Results.Created($"/api/tickets/{id}/attachments/{attachmentId}", new
            {
                id = attachmentId,
                url = $"/api/tickets/{id}/attachments/{attachmentId}",
                mimeType = sniffedMime,
                size = writeResult.SizeBytes,
                filename = safeFilename,
            });
        }).WithName("UploadTicketAttachment").WithOpenApi()
          .DisableRequestTimeout();

        // Generic download for ticket-owned and event-owned attachments
        // (Notes / Comments / outbound-mail attachments staged on the
        // ticket). Attachment must belong either to this ticket directly
        // (owner_kind='Ticket', owner_id=ticketId) or to a ticket-event
        // beneath it (event_id → ticket_events.ticket_id = ticketId).
        // Inbound-mail attachments still flow through TicketMailEndpoints so
        // the per-mail audit row keeps working unchanged.
        group.MapGet("/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, HttpContext http,
            [FromQuery] bool? inline,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IAttachmentRepository attachments, IBlobStore blobs,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

            var att = await attachments.GetByIdAsync(attachmentId, ct);
            if (att is null) return Results.NotFound();
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash))
                return Results.NotFound();

            // Two valid ownership paths:
            //  (a) staged on this ticket (no event yet — used by the editor
            //      while a post is being composed)
            //  (b) attached to a Note/Comment/MailSent event on this ticket
            //      (event_id resolves back to ticket_events.ticket_id via FK)
            var ownsDirect = att.OwnerKind == "Ticket" && att.OwnerId == id && att.EventId is null;
            var ownsViaEvent = att.EventId.HasValue && await tickets.EventBelongsToTicketAsync(id, att.EventId.Value, ct);
            if (!ownsDirect && !ownsViaEvent) return Results.NotFound();

            var stream = await blobs.OpenReadAsync(att.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.attachment.view",
                Actor: http.User.Identity?.Name ?? userId.ToString(),
                ActorRole: role,
                Target: attachmentId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId = id, eventId = att.EventId, filename = att.OriginalFilename }), ct);

            var fileName = string.IsNullOrWhiteSpace(att.OriginalFilename) ? "attachment" : att.OriginalFilename;
            var contentType = string.IsNullOrWhiteSpace(att.MimeType) ? "application/octet-stream" : att.MimeType;
            return inline == true
                ? Results.File(stream, contentType, fileDownloadName: null, enableRangeProcessing: true)
                : Results.File(stream, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
        }).WithName("GetTicketAttachment").WithOpenApi();

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
        // Strip directory components (some browsers send full paths) and
        // refuse anything that the OS would treat specially. Truncate so an
        // adversarial 4 KB filename can't blow the table column.
        var leaf = Path.GetFileName(input);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = leaf.Select(c => invalid.Contains(c) || c == '<' || c == '>' || c == ':' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimStart('.');
        if (string.IsNullOrEmpty(s)) s = "attachment";
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
