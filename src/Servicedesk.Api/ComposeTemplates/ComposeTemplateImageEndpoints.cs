using Servicedesk.Api.Tickets;
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
            // Templates embed pictures only; the sniffed type (not the
            // client-supplied one) decides.
            var received = await UploadReceiver.ReceiveAsync(http, blobs, settings, ct, imagesOnly: true);
            if (received.Error is not null) return received.Error;
            var (safeFilename, sniffedMime, writeResult) = received.Ok!.Value;

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
          .WithName("UploadComposeTemplateImage").WithOpenApi();

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

            var etag = Servicedesk.Api.Tickets.AttachmentResponse.ETag(att.ContentHash, att.MimeType);
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
}
