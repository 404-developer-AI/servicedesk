using Servicedesk.Domain.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.KnowledgeBase;

/// Anonymous reader for Published KB articles — the target of the public
/// links agents insert into outbound mail via the `::` picker. Gated twice:
/// the KnowledgeBase.PublicLinks.Enabled setting (off by default; everything
/// is 404 until an admin opts in) and the article status (only Published is
/// ever served; Draft/Internal/Archived answer exactly like an unknown id so
/// the response shape leaks nothing). GET-only, so the CSRF middleware's
/// safe-method pass-through applies — no exempt-prefix entry needed.
/// Rate-limited per {ip, article} via the "kb-public" policy (Program.cs).
public static class KbPublicEndpoints
{
    public static IEndpointRouteBuilder MapKbPublicEndpoints(this IEndpointRouteBuilder app)
    {
        // NOTE: intentionally NOT calling RequireAuthorization — same model
        // as the public intake-form and survey endpoints.
        var group = app.MapGroup("/api/public/kb")
            .WithTags("KnowledgeBasePublic")
            .RequireRateLimiting("kb-public");

        group.MapGet("/articles/{id:guid}", async (
            Guid id, HttpContext http,
            ISettingsService settings,
            IKbConfigRepository configs, IKbArticleRepository repo,
            CancellationToken ct) =>
        {
            if (!await settings.GetAsync<bool>(SettingKeys.KnowledgeBase.PublicLinksEnabled, ct))
                return Results.NotFound();

            var article = await repo.GetArticleAsync(id, ct);
            if (article is null || article.Status != KbArticleStatus.Published)
                return Results.NotFound();

            var config = await configs.GetConfigAsync(ct);
            var translation = await repo.GetTranslationAsync(article.Id, config.DefaultLocaleCode, ct);
            if (translation is null) return Results.NotFound();

            // Inline images in the stored body point at the authenticated
            // attachment route; rewrite them to the public twin below so a
            // customer's browser can actually load them.
            var bodyHtml = translation.BodyHtml.Replace(
                $"/api/kb/articles/{id}/attachments/",
                $"/api/public/kb/articles/{id}/attachments/",
                StringComparison.OrdinalIgnoreCase);

            // Public links are for customers who received them, not for
            // search engines crawling the helpdesk.
            http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

            return Results.Ok(new
            {
                id = article.Id,
                slug = article.Slug,
                title = translation.Title,
                bodyHtml,
                updatedUtc = article.UpdatedUtc,
            });
        }).WithName("GetPublicKbArticle").WithOpenApi();

        // Public twin of GetKbAttachment, with the same cross-article guess
        // refusal plus the Published + toggle gates of the article route —
        // an image uploaded to an Internal article must never leak through
        // a Published article's id (OwnerId check) or at all (status check).
        group.MapGet("/articles/{id:guid}/attachments/{attachmentId:guid}", async (
            Guid id, Guid attachmentId, HttpContext http,
            bool? inline,
            ISettingsService settings,
            IKbArticleRepository articleRepo,
            IAttachmentRepository attachments, IBlobStore blobs,
            CancellationToken ct) =>
        {
            if (!await settings.GetAsync<bool>(SettingKeys.KnowledgeBase.PublicLinksEnabled, ct))
                return Results.NotFound();

            var article = await articleRepo.GetArticleAsync(id, ct);
            if (article is null || article.Status != KbArticleStatus.Published)
                return Results.NotFound();

            var att = await attachments.GetByIdAsync(attachmentId, ct);
            if (att is null) return Results.NotFound();
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash))
                return Results.NotFound();
            if (att.OwnerKind != "KbArticle" || att.OwnerId != id)
                return Results.NotFound();

            var etag = $"\"{att.ContentHash}\"";
            http.Response.Headers.ETag = etag;
            // Cacheable by the customer's browser but article status can
            // change, so revalidate like the authenticated route does.
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
        }).WithName("GetPublicKbAttachment").WithOpenApi();

        return app;
    }
}
