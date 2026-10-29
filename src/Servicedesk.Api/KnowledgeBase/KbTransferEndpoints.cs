using Microsoft.AspNetCore.Http.Features;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.KnowledgeBase;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.KnowledgeBase;

/// Single-article export + import as a zip package (format in
/// <see cref="KbArticlePackage"/>). Same gate as the rest of /api/kb:
/// Agent+Admin with the per-user KB flag. An import always lands as a new
/// Draft — publishing stays the normal status flow — and runs the body
/// through the same sanitiser + body-text pipeline as the editor.
public static class KbTransferEndpoints
{
    // Multipart framing (boundaries, part headers, the sectionId field).
    private const long MultipartOverheadBytes = 1_048_576;

    public static IEndpointRouteBuilder MapKbTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/kb")
            .WithTags("KnowledgeBase")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireKbAccess();

        // GET /api/kb/articles/{id}/export — download the article package.
        group.MapGet("/articles/{id:guid}/export", async (
            Guid id, HttpContext http,
            IKbConfigRepository configs, IKbArticleRepository articles,
            IAttachmentRepository attachments, IBlobStore blobs,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var article = await articles.GetArticleAsync(id, ct);
            if (article is null) return Results.NotFound();

            var config = await configs.GetConfigAsync(ct);
            var translation = await articles.GetTranslationAsync(id, config.DefaultLocaleCode, ct);
            var rows = await attachments.ListByKbArticleAsync(id, ct);

            var export = new KbPackageExport(
                id,
                translation?.Title ?? article.Slug,
                config.DefaultLocaleCode,
                translation?.BodyHtml ?? string.Empty,
                DateTime.UtcNow,
                rows.Where(a => !string.IsNullOrEmpty(a.ContentHash))
                    // Oldest first so asset numbering follows upload order.
                    .Reverse()
                    .Select(a => new KbPackageExportAttachment(a.Id, a.ContentHash!, a.OriginalFilename))
                    .ToList());

            // The package can hold up to the article's attachments in full;
            // staged in a self-deleting temp file instead of memory.
            var temp = new FileStream(
                Path.Combine(Path.GetTempPath(), $"sd-kb-export-{Guid.NewGuid():N}.zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            IReadOnlyList<string> skipped;
            try
            {
                skipped = await KbArticlePackage.WriteAsync(temp, export, blobs, ct);
                temp.Position = 0;
            }
            catch
            {
                await temp.DisposeAsync();
                throw;
            }

            await KbAudit.WriteAsync(audit, http, "kb.article.exported", id.ToString(),
                new { id, article.Slug, assets = export.Attachments.Count - skipped.Count, skipped = skipped.Count });

            return Results.File(temp, "application/zip", $"{article.Slug}.zip");
        }).WithName("ExportKbArticle").WithOpenApi();

        // POST /api/kb/articles/import — multipart: `file` (the .zip) and
        // `sectionId`. Creates a new Draft in that section.
        group.MapPost("/articles/import", async (
            HttpContext http,
            IKbConfigRepository configs, IKbSectionRepository sections,
            IKbArticleRepository articles, IKbHtmlSanitizer sanitizer,
            IAttachmentRepository attachments, IBlobStore blobs,
            ISettingsService settings, IAuditLogger audit, CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required." });

            var maxPackageBytes = await settings.GetAsync<long>(SettingKeys.KnowledgeBase.ImportMaxPackageBytes, ct);
            var maxEntries = await settings.GetAsync<int>(SettingKeys.KnowledgeBase.ImportMaxEntries, ct);
            var maxTotal = await settings.GetAsync<long>(SettingKeys.KnowledgeBase.ImportMaxUncompressedBytes, ct);
            var maxAsset = await settings.GetAsync<long>(SettingKeys.Storage.MaxAttachmentBytes, ct);
            if (maxPackageBytes <= 0 || maxEntries <= 0 || maxTotal <= 0 || maxAsset <= 0)
                return Results.Problem("Knowledge base import limits are not configured.", statusCode: 500);

            var bodyLimit = maxPackageBytes + MultipartOverheadBytes;
            var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = bodyLimit;
            if (http.Request.ContentLength is long advertised && advertised > bodyLimit)
                return PackageTooLarge(maxPackageBytes);

            IFormCollection form;
            try
            {
                form = await http.Request.ReadFormAsync(ct);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return PackageTooLarge(maxPackageBytes);
            }
            catch (InvalidDataException)
            {
                return PackageTooLarge(maxPackageBytes);
            }

            if (!Guid.TryParse(form["sectionId"].ToString(), out var sectionId) || sectionId == Guid.Empty)
                return Results.BadRequest(new { error = "SectionId is required." });
            if (await sections.GetSectionAsync(sectionId, ct) is null)
                return Results.BadRequest(new { error = "Section not found." });

            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Upload the article package (.zip) in the 'file' field." });
            if (file.Length > maxPackageBytes)
                return PackageTooLarge(maxPackageBytes);

            KbPackageReadResult read;
            await using (var zipStream = file.OpenReadStream())
            {
                read = await KbArticlePackage.ReadAsync(
                    zipStream, new KbPackageLimits(maxEntries, maxAsset, maxTotal), blobs, ct);
            }
            if (read.Content is not { } content)
                return Results.BadRequest(new { error = read.Error });

            var config = await configs.GetConfigAsync(ct);
            var locale = config.DefaultLocaleCode;
            if (!string.IsNullOrWhiteSpace(content.LocaleCode)
                && await configs.GetLocaleAsync(content.LocaleCode, ct) is { IsActive: true } packageLocale)
            {
                locale = packageLocale.Code;
            }

            var slug = await KbArticleEndpoints.ResolveArticleSlugAsync(null, content.Title, sectionId, articles, ct)
                ?? KbSlugGenerator.Slugify(content.Title);
            var actorUserId = ActorContext.GetUserId(http);
            var created = await articles.CreateArticleAsync(
                sectionId, slug, KbArticleStatus.Draft, null, 0, actorUserId, ct);

            KbArticleTranslation translation;
            IReadOnlyList<string> unresolved;
            try
            {
                var urlByPath = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var asset in content.Assets)
                {
                    var attachmentId = await attachments.CreateForKbArticleAsync(new NewKbArticleAttachment(
                        ArticleId: created.Id,
                        ContentHash: asset.Write.ContentHash,
                        SizeBytes: asset.Write.SizeBytes,
                        MimeType: asset.MimeType,
                        OriginalFilename: Tickets.UploadReceiver.SanitizeFilename(asset.Filename)), ct);
                    urlByPath[asset.Path] = $"/api/kb/articles/{created.Id}/attachments/{attachmentId}";
                }

                var (rewritten, missing) = KbArticlePackage.RewriteForImport(content.BodyHtml, urlByPath);
                unresolved = missing;
                var sanitizedHtml = sanitizer.Sanitize(rewritten);
                var bodyText = KbBodyStripper.HtmlToText(sanitizedHtml);
                translation = await articles.UpsertTranslationAsync(
                    created.Id, locale, content.Title, sanitizedHtml, bodyText, ct);
            }
            catch
            {
                // Never leave a half-imported draft behind; attachment rows
                // go with the article, the blobs are swept by the GC.
                await articles.HardDeleteArticleAsync(created.Id, CancellationToken.None);
                throw;
            }

            await KbAudit.WriteAsync(audit, http, "kb.article.imported", created.Id.ToString(),
                new
                {
                    created.Id, created.SectionId, created.Slug, locale,
                    package = Tickets.UploadReceiver.SanitizeFilename(file.FileName),
                    assets = content.Assets.Count,
                    unresolvedReferences = unresolved.Count,
                });

            var warnings = unresolved
                .Select(p => $"The body references '{p}', which is not in the package.")
                .ToList();
            return Results.Created($"/api/kb/articles/{created.Id}",
                new { article = created, translation, assets = content.Assets.Count, warnings });
        }).WithName("ImportKbArticle").WithOpenApi();

        return app;
    }

    private static IResult PackageTooLarge(long maxBytes) =>
        Results.Json(new
        {
            error = $"The package exceeds the {maxBytes / 1_048_576} MB limit ({SettingKeys.KnowledgeBase.ImportMaxPackageBytes}).",
        }, statusCode: StatusCodes.Status413PayloadTooLarge);
}
