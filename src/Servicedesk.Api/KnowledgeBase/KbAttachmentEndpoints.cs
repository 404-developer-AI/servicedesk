using Servicedesk.Api.Tickets;
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
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireKbAccess();

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

            var received = await UploadReceiver.ReceiveAsync(http, blobs, settings, ct);
            if (received.Error is not null) return received.Error;
            var (safeFilename, sniffedMime, writeResult) = received.Ok!.Value;

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
        }).WithName("UploadKbAttachment").WithOpenApi();

        // GET /api/kb/articles/{id}/attachments — list the article's attachments
        // (newest first). Backs the article-edit "Attachments" panel; the client
        // filters out inline images from the panel itself.
        group.MapGet("/articles/{id:guid}/attachments", async (
            Guid id,
            IKbArticleRepository articleRepo,
            IAttachmentRepository attachments,
            CancellationToken ct) =>
        {
            var article = await articleRepo.GetArticleAsync(id, ct);
            if (article is null) return Results.NotFound();

            var rows = await attachments.ListByKbArticleAsync(id, ct);
            return Results.Ok(new
            {
                items = rows.Select(a => new
                {
                    id = a.Id,
                    url = $"/api/kb/articles/{id}/attachments/{a.Id}",
                    mimeType = a.MimeType,
                    size = a.SizeBytes,
                    filename = a.OriginalFilename,
                }),
            });
        }).WithName("ListKbAttachments").WithOpenApi();

        // DELETE /api/kb/articles/{id}/attachments/{attachmentId} — remove an
        // attachment from the panel. Scoped to the owning article. Any link to
        // it left in the body becomes a dead link until the author edits it out
        // (intentional — the body is the author's to manage).
        group.MapDelete("/articles/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, HttpContext http,
            IKbArticleRepository articleRepo,
            IAttachmentRepository attachments, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var article = await articleRepo.GetArticleAsync(id, ct);
            if (article is null) return Results.NotFound();

            var deleted = await attachments.DeleteKbAttachmentAsync(attachmentId, id, ct);
            if (!deleted) return Results.NotFound();

            await KbAudit.WriteAsync(audit, http, "kb.article.attachment.removed", attachmentId.ToString(),
                new { articleId = id });

            return Results.NoContent();
        }).WithName("DeleteKbAttachment").WithOpenApi();

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

            var etag = Servicedesk.Api.Tickets.AttachmentResponse.ETag(att.ContentHash, att.MimeType);
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, max-age=604800, must-revalidate";
            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag)))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var stream = await blobs.OpenReadAsync(att.ContentHash, ct);
            if (stream is null) return Results.NotFound();

            // Inline only for inline-safe types (audit v0.1.1 #2) — KB
            // uploads are sniffed, but the shared guard keeps every
            // attachment route on the same rule.
            return Servicedesk.Api.Tickets.AttachmentResponse.File(
                stream, att.MimeType, att.OriginalFilename, inline == true);
        }).WithName("GetKbAttachment").WithOpenApi();

        return app;
    }
}
