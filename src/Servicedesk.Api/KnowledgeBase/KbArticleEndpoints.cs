using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.KnowledgeBase;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.KnowledgeBase;

/// Article CRUD + status flips + featured toggle + section/position move.
/// Reads are Agent+Admin so an agent can browse/search the KB standalone
/// via /kb. Article create + edit + Draft↔Internal status flips are
/// available to both agents and admins (collaborative editing). Flips
/// to/from Published or Archived stay Admin-only — those land on the
/// public-facing tier in v0.1.x.
public static class KbArticleEndpoints
{
    public static IEndpointRouteBuilder MapKbArticleEndpoints(this IEndpointRouteBuilder app)
    {
        var agentGroup = app.MapGroup("/api/kb")
            .WithTags("KnowledgeBase")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireKbAccess();

        // ---- Reads (Agent+Admin) ----

        agentGroup.MapGet("/articles", async (
            Guid? sectionId, string? status, string? search,
            int? page, int? pageSize,
            IKbArticleRepository repo, CancellationToken ct) =>
        {
            var result = await repo.ListArticlesAsync(
                sectionId, status, search, page ?? 1, pageSize ?? 50, ct);
            return Results.Ok(result);
        }).WithName("ListKbArticles").WithOpenApi();

        agentGroup.MapGet("/articles/{id:guid}", async (
            Guid id, string? include,
            IKbConfigRepository configs, IKbArticleRepository repo, CancellationToken ct) =>
        {
            var article = await repo.GetArticleAsync(id, ct);
            if (article is null) return Results.NotFound();

            var config = await configs.GetConfigAsync(ct);
            KbArticleTranslation? translation = null;
            if (string.Equals(include, "body", StringComparison.OrdinalIgnoreCase))
            {
                translation = await repo.GetTranslationAsync(article.Id, config.DefaultLocaleCode, ct);
            }
            return Results.Ok(new { article, translation });
        }).WithName("GetKbArticle").WithOpenApi();

        agentGroup.MapGet("/articles/by-slug/{sectionSlug}/{articleSlug}", async (
            string sectionSlug, string articleSlug,
            IKbArticleRepository repo, CancellationToken ct) =>
        {
            var hit = await repo.GetArticleBySlugAsync(sectionSlug, articleSlug, ct);
            return hit is null ? Results.NotFound() : Results.Ok(hit);
        }).WithName("GetKbArticleBySlug").WithOpenApi();

        agentGroup.MapGet("/featured", async (
            int? limit, IKbArticleRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListFeaturedAsync(limit ?? 6, ct)))
            .WithName("ListKbFeatured").WithOpenApi();

        // v0.0.75 — tells the `::` picker whether public article links are
        // switched on and which base URL to build them with. Agent-side
        // only; when disabled the picker hides the KB source entirely so
        // nobody inserts a link that would 404 for the customer.
        agentGroup.MapGet("/public-link-config", async (
            ISettingsService settings, CancellationToken ct) =>
        {
            var enabled = await settings.GetAsync<bool>(SettingKeys.KnowledgeBase.PublicLinksEnabled, ct);
            var baseUrl = (await settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct))?.TrimEnd('/') ?? string.Empty;
            return Results.Ok(new { enabled, publicBaseUrl = baseUrl });
        }).WithName("GetKbPublicLinkConfig").WithOpenApi();

        // ---- Writes (Agent+Admin per status-rules below) ----

        agentGroup.MapPost("/articles", async (
            [FromBody] KbArticleCreateRequest req, HttpContext http,
            IKbConfigRepository configs, IKbSectionRepository sections,
            IKbArticleRepository articles, IKbHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.SectionId == Guid.Empty)
                return Results.BadRequest(new { error = "SectionId is required." });
            if (await sections.GetSectionAsync(req.SectionId, ct) is null)
                return Results.BadRequest(new { error = "Section not found." });
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required." });

            // Slug is auto-derived from the title with -2/-3/… clash-suffix
            // for siblings within the same section. An explicit slug from
            // an older client is still accepted but validated + clash-checked.
            var slug = await ResolveArticleSlugAsync(req.Slug, req.Title, req.SectionId, articles, ct);
            if (slug is null)
                return Results.BadRequest(new { error = "Slug must be lowercase ASCII letters/digits separated by single hyphens." });

            var actorUserId = ActorContext.GetUserId(http);
            var created = await articles.CreateArticleAsync(
                req.SectionId, slug, KbArticleStatus.Draft,
                NormalizeNotes(req.EditorNotes), req.Position ?? 0,
                actorUserId, ct);

            var config = await configs.GetConfigAsync(ct);
            var locale = string.IsNullOrWhiteSpace(req.LocaleCode) ? config.DefaultLocaleCode : req.LocaleCode!;
            var sanitizedHtml = sanitizer.Sanitize(req.BodyHtml ?? string.Empty);
            var bodyText = KbBodyStripper.HtmlToText(sanitizedHtml);
            var translation = await articles.UpsertTranslationAsync(
                created.Id, locale, req.Title!.Trim(), sanitizedHtml, bodyText, ct);

            await KbAudit.WriteAsync(audit, http, "kb.article.created", created.Id.ToString(),
                new { created.Id, created.SectionId, created.Slug, locale });
            return Results.Created($"/api/kb/articles/{created.Id}", new { article = created, translation });
        }).WithName("CreateKbArticle").WithOpenApi();

        agentGroup.MapPut("/articles/{id:guid}", async (
            Guid id, [FromBody] KbArticleUpdateRequest req, HttpContext http,
            IKbConfigRepository configs, IKbSectionRepository sections,
            IKbArticleRepository articles, IKbHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await articles.GetArticleAsync(id, ct);
            if (existing is null) return Results.NotFound();

            // Agents may edit any article that's still in Draft or Internal —
            // collaborative editing is the point of a KB. Once Published or
            // Archived an article is admin-territory until it flips back.
            var role = http.User.FindFirst(ClaimTypes.Role)?.Value ?? "";
            if (role != "Admin" && !KbArticleStatus.IsAgentReachable(existing.Status))
                return Results.Forbid();

            var sectionId = req.SectionId ?? existing.SectionId;
            if (sectionId != existing.SectionId &&
                await sections.GetSectionAsync(sectionId, ct) is null)
                return Results.BadRequest(new { error = "Target section not found." });

            // Slug is auto-managed: omitted slug means "keep the existing
            // one" (URL stability across title edits). An explicit override
            // is still accepted but format-validated.
            var slug = existing.Slug;
            if (!string.IsNullOrWhiteSpace(req.Slug))
            {
                slug = req.Slug.Trim().ToLowerInvariant();
                if (!IsValidSlug(slug))
                    return Results.BadRequest(new { error = "Slug must be lowercase ASCII letters/digits separated by single hyphens." });
            }

            var actorUserId = ActorContext.GetUserId(http);
            var updated = await articles.UpdateArticleAsync(
                id, sectionId, slug,
                req.EditorNotes is null ? existing.EditorNotes : NormalizeNotes(req.EditorNotes),
                req.Position ?? existing.Position,
                actorUserId, ct);
            if (updated is null) return Results.NotFound();

            if (req.Title is not null || req.BodyHtml is not null)
            {
                var config = await configs.GetConfigAsync(ct);
                var locale = string.IsNullOrWhiteSpace(req.LocaleCode) ? config.DefaultLocaleCode : req.LocaleCode!;
                var existingTranslation = await articles.GetTranslationAsync(id, locale, ct);
                var title = (req.Title ?? existingTranslation?.Title ?? updated.Slug).Trim();
                var sanitizedHtml = sanitizer.Sanitize(req.BodyHtml ?? existingTranslation?.BodyHtml ?? string.Empty);
                var bodyText = KbBodyStripper.HtmlToText(sanitizedHtml);
                await articles.UpsertTranslationAsync(id, locale, title, sanitizedHtml, bodyText, ct);
            }

            await KbAudit.WriteAsync(audit, http, "kb.article.updated", id.ToString(),
                new { id, updated.SectionId, updated.Slug });
            return Results.Ok(updated);
        }).WithName("UpdateKbArticle").WithOpenApi();

        agentGroup.MapPost("/articles/{id:guid}/status", async (
            Guid id, [FromBody] KbArticleStatusRequest req, HttpContext http,
            IKbArticleRepository articles, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Status) || !KbArticleStatus.IsValid(req.Status))
                return Results.BadRequest(new { error = "Status must be one of Draft, Internal, Published, Archived." });

            var existing = await articles.GetArticleAsync(id, ct);
            if (existing is null) return Results.NotFound();

            // Status-flip authorisation:
            //   Admin → any flip is allowed.
            //   Agent → only Draft↔Internal flips, on either own or other-author drafts.
            //
            // Anything that lands on or moves away from Published/Archived
            // requires Admin. The check is server-side; the UI hides the
            // controls but defence-in-depth.
            var role = http.User.FindFirst(ClaimTypes.Role)?.Value ?? "";
            if (role != "Admin")
            {
                var bothAgentReachable =
                    KbArticleStatus.IsAgentReachable(existing.Status) &&
                    KbArticleStatus.IsAgentReachable(req.Status);
                if (!bothAgentReachable)
                    return Results.Forbid();
            }

            var actorUserId = ActorContext.GetUserId(http);
            var flipped = await articles.FlipStatusAsync(id, req.Status, actorUserId, ct);
            if (flipped is null) return Results.NotFound();

            await KbAudit.WriteAsync(audit, http, "kb.article.status.changed", id.ToString(),
                new { id, from = existing.Status, to = req.Status });
            return Results.Ok(flipped);
        }).WithName("ChangeKbArticleStatus").WithOpenApi();

        // ---- Admin-only writes ----

        var adminGroup = app.MapGroup("/api/kb")
            .WithTags("KnowledgeBase")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .RequireKbAccess();

        adminGroup.MapPost("/articles/{id:guid}/featured", async (
            Guid id, [FromBody] KbArticleFeaturedRequest req, HttpContext http,
            IKbArticleRepository articles, IAuditLogger audit, CancellationToken ct) =>
        {
            var actorUserId = ActorContext.GetUserId(http);
            var updated = await articles.SetFeaturedAsync(id, req.IsFeatured ?? false, actorUserId, ct);
            if (updated is null) return Results.NotFound();
            await KbAudit.WriteAsync(audit, http,
                req.IsFeatured == true ? "kb.article.featured.set" : "kb.article.featured.unset",
                id.ToString(), new { id });
            return Results.Ok(updated);
        }).WithName("SetKbArticleFeatured").WithOpenApi();

        adminGroup.MapPost("/articles/{id:guid}/move", async (
            Guid id, [FromBody] KbArticleMoveRequest req, HttpContext http,
            IKbSectionRepository sections, IKbArticleRepository articles,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.SectionId == Guid.Empty)
                return Results.BadRequest(new { error = "SectionId is required." });
            if (await sections.GetSectionAsync(req.SectionId, ct) is null)
                return Results.BadRequest(new { error = "Target section not found." });

            var existing = await articles.GetArticleAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var actorUserId = ActorContext.GetUserId(http);
            var moved = await articles.MoveArticleAsync(
                id, req.SectionId, req.Position ?? existing.Position, actorUserId, ct);
            if (moved is null) return Results.NotFound();

            await KbAudit.WriteAsync(audit, http, "kb.article.moved", id.ToString(),
                new { id, from = existing.SectionId, to = req.SectionId, position = req.Position });
            return Results.Ok(moved);
        }).WithName("MoveKbArticle").WithOpenApi();

        adminGroup.MapDelete("/articles/{id:guid}", async (
            Guid id, bool? hard, HttpContext http,
            IKbArticleRepository articles, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await articles.GetArticleAsync(id, ct);
            if (existing is null) return Results.NotFound();

            // Soft delete = flip to Archived. Hard delete is reserved for
            // articles that never reached Published; same rule the design
            // doc spells out.
            if (hard == true)
            {
                if (existing.Status == KbArticleStatus.Published)
                    return Results.Conflict(new
                    {
                        error = "Cannot hard-delete a Published article. Archive it first or contact an admin.",
                    });
                var removed = await articles.HardDeleteArticleAsync(id, ct);
                if (!removed) return Results.NotFound();
                await KbAudit.WriteAsync(audit, http, "kb.article.deleted", id.ToString(),
                    new { id, mode = "hard", existing.Status });
                return Results.NoContent();
            }

            if (existing.Status == KbArticleStatus.Archived) return Results.NoContent();

            var actorUserId = ActorContext.GetUserId(http);
            var archived = await articles.FlipStatusAsync(id, KbArticleStatus.Archived, actorUserId, ct);
            if (archived is null) return Results.NotFound();
            await KbAudit.WriteAsync(audit, http, "kb.article.deleted", id.ToString(),
                new { id, mode = "soft", from = existing.Status });
            return Results.NoContent();
        }).WithName("DeleteKbArticle").WithOpenApi();

        return app;
    }

    // Shared, single-compiled matcher. NonBacktracking guarantees linear-time
    // evaluation (no ReDoS), and the length cap rejects absurd input before the
    // regex even runs — 200 is far above any real article slug.
    private static readonly global::System.Text.RegularExpressions.Regex SlugPattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        global::System.Text.RegularExpressions.RegexOptions.NonBacktracking
        | global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsValidSlug(string slug) =>
        slug.Length <= 200 && SlugPattern.IsMatch(slug);

    /// Resolve a clash-free slug for a new article. Empty/missing input
    /// derives from the title; an explicit override is validated. The
    /// suffix loop walks `-2`, `-3`, … until it finds an unused slot or
    /// gives up at 100 (the DB UNIQUE catches the residual race).
    private static async Task<string?> ResolveArticleSlugAsync(
        string? requestedSlug, string title, Guid sectionId,
        IKbArticleRepository articles, CancellationToken ct)
    {
        string baseSlug;
        if (!string.IsNullOrWhiteSpace(requestedSlug))
        {
            baseSlug = requestedSlug.Trim().ToLowerInvariant();
            if (!IsValidSlug(baseSlug)) return null;
        }
        else
        {
            baseSlug = KbSlugGenerator.Slugify(title);
        }

        var candidate = baseSlug;
        var suffix = 2;
        while (await articles.ArticleSlugExistsInSectionAsync(sectionId, candidate, ct))
        {
            candidate = $"{baseSlug}-{suffix++}";
            if (suffix > 100) return baseSlug;
        }
        return candidate;
    }

    private static string? NormalizeNotes(string? notes)
        => string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
}

public sealed record KbArticleCreateRequest(
    Guid SectionId,
    string? Slug,
    string? Title,
    string? BodyHtml,
    string? EditorNotes,
    int? Position,
    string? LocaleCode);

public sealed record KbArticleUpdateRequest(
    Guid? SectionId,
    string? Slug,
    string? Title,
    string? BodyHtml,
    string? EditorNotes,
    int? Position,
    string? LocaleCode);

public sealed record KbArticleStatusRequest(string? Status);

public sealed record KbArticleFeaturedRequest(bool? IsFeatured);

public sealed record KbArticleMoveRequest(Guid SectionId, int? Position);
