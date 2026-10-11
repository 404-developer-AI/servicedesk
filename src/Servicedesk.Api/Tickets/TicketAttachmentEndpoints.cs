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
            var ticket = await tickets.GetCoreAsync(id, ct);
            if (ticket is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.QueueId, ct))
                return Results.NotFound();

            var stored = await StoreUploadedFileAsync(http, id, attachments, blobs, settings, ct);
            if (stored.Error is not null) return stored.Error;
            var (attachmentId, safeFilename, sniffedMime, writeResult) = stored.Ok!.Value;

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
        }).WithName("UploadTicketAttachment").WithOpenApi();

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
            [FromQuery] bool? open,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            IAttachmentRepository attachments, IBlobStore blobs,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var ticket = await tickets.GetCoreAsync(id, ct);
            if (ticket is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.QueueId, ct))
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

            // Blobs are content-addressed so the sha256 is a stable strong
            // ETag for the lifetime of this attachment row. A matching
            // If-None-Match short-circuits: no blob-open, no body, no audit
            // row — which matters because a quoted-reply thread can embed
            // the same inline image across every event, and incognito
            // browsers (no HTTP cache) would otherwise fire one GET per
            // <img>. must-revalidate keeps the ownership check honest after
            // the TTL expires; private because the body is auth-gated.
            var etag = AttachmentResponse.ETag(att.ContentHash, att.MimeType);
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, max-age=604800, must-revalidate";
            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var stream = await blobs.OpenReadAsync(att.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            if (!AttachmentResponse.IsUnauditedInlineImage(inline == true && open != true, att.MimeType))
            {
                await audit.LogAsync(new AuditEvent(
                    EventType: "ticket.attachment.view",
                    Actor: http.User.Identity?.Name ?? userId.ToString(),
                    ActorRole: role,
                    Target: attachmentId.ToString(),
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new { ticketId = id, eventId = att.EventId, filename = att.OriginalFilename }), ct);
            }

            // Inline only for inline-safe types (audit v0.1.1 #2) — see
            // AttachmentResponse for the reasoning.
            return AttachmentResponse.File(stream, att.MimeType, att.OriginalFilename, inline == true);
        }).WithName("GetTicketAttachment").WithOpenApi();

        return app;
    }

    /// Result of <see cref="StoreUploadedFileAsync"/>: either an error
    /// response to return verbatim, or the stored attachment row.
    internal readonly record struct StoredUpload(
        IResult? Error,
        (Guid AttachmentId, string Filename, string MimeType, BlobWriteResult Write)? Ok);

    /// The ticket upload core: <see cref="UploadReceiver"/> (limits, MIME
    /// sniff, HTML refusal, streamed blob write) plus the staged row insert.
    /// Used by the agent endpoint above and by the customer-portal upload
    /// (v0.1.0) so both apply identical validation. Authorization is the
    /// caller's job — this only stores.
    internal static async Task<StoredUpload> StoreUploadedFileAsync(
        HttpContext http, Guid ticketId,
        IAttachmentRepository attachments, IBlobStore blobs, ISettingsService settings,
        CancellationToken ct)
    {
        var received = await UploadReceiver.ReceiveAsync(http, blobs, settings, ct);
        if (received.Error is not null) return new(received.Error, null);
        var (filename, mimeType, write) = received.Ok!.Value;

        var attachmentId = await attachments.CreateUploadedAsync(new NewUploadedAttachment(
            TicketId: ticketId,
            ContentHash: write.ContentHash,
            SizeBytes: write.SizeBytes,
            MimeType: mimeType,
            OriginalFilename: filename), ct);
        return new(null, (attachmentId, filename, mimeType, write));
    }
}
