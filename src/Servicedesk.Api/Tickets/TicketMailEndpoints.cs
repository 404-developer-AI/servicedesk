using System.Security.Claims;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Tickets;

/// Raw .eml download for ingested mail. Streams the content-addressed blob
/// as <c>message/rfc822</c>. Access requires Agent role + queue-access.
/// Every successful view is audit-logged as <c>mail.raw.view</c>.
public static class TicketMailEndpoints
{
    public static IEndpointRouteBuilder MapTicketMailEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/{id:guid}/mail/{mailMessageId:guid}/raw", async (
            Guid id, Guid mailMessageId, HttpContext http,
            ITicketRepository tickets, IMailMessageRepository mail,
            IBlobStore blobs, IQueueAccessService queueAccess, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

            var row = await mail.GetByIdAsync(mailMessageId, ct);
            if (row is null || row.TicketId != id) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(row.RawEmlBlobHash)) return Results.NotFound();

            var stream = await blobs.OpenReadAsync(row.RawEmlBlobHash, ct);
            if (stream is null) return Results.NotFound();

            await audit.LogAsync(new AuditEvent(
                EventType: "mail.raw.view",
                Actor: http.User.Identity?.Name ?? userId.ToString(),
                ActorRole: role,
                Target: mailMessageId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId = id, from = row.FromAddress, subject = row.Subject }), ct);

            var fileName = SanitizeFilename(row.MessageId) + ".eml";
            return Results.Stream(stream, contentType: "message/rfc822", fileDownloadName: fileName);
        }).WithName("GetTicketMailRaw").WithOpenApi();

        // ── Attachment download ──
        //
        // Chain of ownership checked end-to-end: attachment → mail → ticket.
        // Any break (wrong ticket, wrong mail, not yet Ready, mismatched owner
        // kind) returns 404 — never leaks whether a resource exists.
        group.MapGet("/{id:guid}/mail/{mailMessageId:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid mailMessageId, Guid attachmentId, HttpContext http,
            ITicketRepository tickets, IMailMessageRepository mail,
            IAttachmentRepository attachments, IBlobStore blobs,
            IQueueAccessService queueAccess, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var ticket = await tickets.GetByIdAsync(id, ct);
            if (ticket is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
                return Results.NotFound();

            var att = await attachments.GetByIdAsync(attachmentId, ct);
            if (att is null) return Results.NotFound();
            if (att.OwnerKind != "Mail" || att.OwnerId != mailMessageId) return Results.NotFound();
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash))
                return Results.NotFound();

            var mailRow = await mail.GetByIdAsync(mailMessageId, ct);
            if (mailRow is null || mailRow.TicketId != id) return Results.NotFound();

            var stream = await blobs.OpenReadAsync(att.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            await audit.LogAsync(new AuditEvent(
                EventType: "mail.attachment.view",
                Actor: http.User.Identity?.Name ?? userId.ToString(),
                ActorRole: role,
                Target: attachmentId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { ticketId = id, mailMessageId, filename = att.OriginalFilename }), ct);

            var fileName = SanitizeFilename(att.OriginalFilename);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "attachment";
            return Results.Stream(stream,
                contentType: string.IsNullOrWhiteSpace(att.MimeType) ? "application/octet-stream" : att.MimeType,
                fileDownloadName: fileName);
        }).WithName("GetTicketMailAttachment").WithOpenApi();

        return app;
    }

    private static string SanitizeFilename(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(c => invalid.Contains(c) || c == '<' || c == '>' ? '_' : c).ToArray();
        var s = new string(chars);
        return s.Length > 80 ? s[..80] : s;
    }
}
