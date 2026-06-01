using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.KnowledgeBase;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;

namespace Servicedesk.Api.KnowledgeBase;

/// Section CRUD + tree-listing. Reads are Agent+Admin so an agent can
/// browse the KB structure standalone via /kb. Mutations are Admin-only —
/// agents create articles inside a section but can't reorganise the tree.
public static class KbSectionEndpoints
{
    public static IEndpointRouteBuilder MapKbSectionEndpoints(this IEndpointRouteBuilder app)
    {
        var readGroup = app.MapGroup("/api/kb")
            .WithTags("KnowledgeBase")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .RequireKbAccess();

        readGroup.MapGet("/sections", async (
            IKbConfigRepository configs, IKbSectionRepository sections, CancellationToken ct) =>
        {
            // Single load + group in memory: typical KB has <50 sections so a
            // recursive CTE adds complexity for no measurable saving.
            var config = await configs.GetConfigAsync(ct);
            var allSections = await sections.ListSectionsAsync(ct);
            var allTranslations = await sections.ListAllTranslationsAsync(ct);
            var translationsBySection = allTranslations
                .GroupBy(t => t.SectionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var tree = BuildTree(allSections, translationsBySection, config.DefaultLocaleCode);
            return Results.Ok(new { config, tree });
        }).WithName("ListKbSections").WithOpenApi();

        readGroup.MapGet("/sections/{id:guid}", async (
            Guid id, IKbConfigRepository configs, IKbSectionRepository sections, CancellationToken ct) =>
        {
            var section = await sections.GetSectionAsync(id, ct);
            if (section is null) return Results.NotFound();
            var translations = await sections.ListTranslationsAsync(id, ct);
            return Results.Ok(new { section, translations });
        }).WithName("GetKbSection").WithOpenApi();

        // ---- Admin-only: section lifecycle ----
        var adminGroup = app.MapGroup("/api/kb")
            .WithTags("KnowledgeBase")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .RequireKbAccess();

        adminGroup.MapPost("/sections", async (
            [FromBody] KbSectionRequest req, HttpContext http,
            IKbConfigRepository configs, IKbSectionRepository sections,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required." });

            // Slug is auto-generated from the title; sibling-clashes get a
            // -2/-3/… suffix transparently. The legacy admin-supplied slug
            // path stays available so an existing client that sends an
            // explicit slug keeps working — the override is validated and
            // checked for clashes the same way.
            var slug = await ResolveSectionSlugAsync(req.Slug, req.Title, req.ParentSectionId, sections, ct);
            if (slug is null)
                return Results.BadRequest(new { error = "Slug must be lowercase ASCII letters/digits separated by single hyphens." });

            var actorUserId = ActorContext.GetUserId(http);
            var created = await sections.CreateSectionAsync(
                req.ParentSectionId, slug, NormalizeIcon(req.IconName), req.Position ?? 0,
                actorUserId, ct);

            var config = await configs.GetConfigAsync(ct);
            var locale = string.IsNullOrWhiteSpace(req.LocaleCode) ? config.DefaultLocaleCode : req.LocaleCode!;
            var translation = await sections.UpsertTranslationAsync(
                created.Id, locale, req.Title!.Trim(),
                string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim(), ct);

            await KbAudit.WriteAsync(audit, http, "kb.section.created", created.Id.ToString(),
                new { created.Id, created.Slug, created.ParentSectionId, locale });
            return Results.Created($"/api/kb/sections/{created.Id}", new { section = created, translation });
        }).WithName("CreateKbSection").WithOpenApi();

        adminGroup.MapPut("/sections/{id:guid}", async (
            Guid id, [FromBody] KbSectionRequest req, HttpContext http,
            IKbConfigRepository configs, IKbSectionRepository sections,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await sections.GetSectionAsync(id, ct);
            if (existing is null) return Results.NotFound();

            // Slugs are auto-managed: a missing slug on update means "keep
            // the existing one" (URL stability). An explicit override is
            // still accepted from older clients but validated + clash-checked.
            string slug;
            if (string.IsNullOrWhiteSpace(req.Slug))
            {
                slug = existing.Slug;
            }
            else
            {
                slug = req.Slug.Trim().ToLowerInvariant();
                if (!IsValidSlug(slug))
                    return Results.BadRequest(new { error = "Slug must be lowercase ASCII letters/digits separated by single hyphens." });
                if (!string.Equals(slug, existing.Slug, StringComparison.OrdinalIgnoreCase))
                {
                    var clash = await sections.GetSectionBySlugAsync(existing.ParentSectionId, slug, ct);
                    if (clash is not null && clash.Id != id)
                        return Results.Conflict(new { error = "A sibling section already uses this slug." });
                }
            }

            var actorUserId = ActorContext.GetUserId(http);
            var updated = await sections.UpdateSectionAsync(
                id, slug, NormalizeIcon(req.IconName), req.Position ?? existing.Position,
                actorUserId, ct);

            if (!string.IsNullOrWhiteSpace(req.Title))
            {
                var config = await configs.GetConfigAsync(ct);
                var locale = string.IsNullOrWhiteSpace(req.LocaleCode) ? config.DefaultLocaleCode : req.LocaleCode!;
                await sections.UpsertTranslationAsync(
                    id, locale, req.Title!.Trim(),
                    string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim(), ct);
            }

            await KbAudit.WriteAsync(audit, http, "kb.section.updated", id.ToString(), new { id, slug });
            return Results.Ok(updated);
        }).WithName("UpdateKbSection").WithOpenApi();

        adminGroup.MapDelete("/sections/{id:guid}", async (
            Guid id, HttpContext http,
            IKbSectionRepository sections, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await sections.DeleteSectionAsync(id, ct);
            return result switch
            {
                SectionDeleteResult.NotFound => Results.NotFound(),
                SectionDeleteResult.NotEmpty => Results.Conflict(new
                {
                    error = "Section is not empty. Move or delete its child sections and articles first.",
                }),
                _ => await DeletedOk(http, audit, id),
            };
        }).WithName("DeleteKbSection").WithOpenApi();

        adminGroup.MapPost("/sections/{id:guid}/move", async (
            Guid id, [FromBody] KbSectionMoveRequest req, HttpContext http,
            IKbSectionRepository sections, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await sections.GetSectionAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var actorUserId = ActorContext.GetUserId(http);
            try
            {
                var moved = await sections.MoveSectionAsync(
                    id, req.ParentSectionId, req.Position ?? existing.Position, actorUserId, ct);
                if (moved is null) return Results.NotFound();
                await KbAudit.WriteAsync(audit, http, "kb.section.moved", id.ToString(),
                    new { id, from = existing.ParentSectionId, to = req.ParentSectionId, position = req.Position });
                return Results.Ok(moved);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("MoveKbSection").WithOpenApi();

        return app;
    }

    // Shared, single-compiled matcher. NonBacktracking guarantees linear-time
    // evaluation (no ReDoS), and the length cap rejects absurd input before the
    // regex even runs — 200 is far above any real section slug.
    private static readonly global::System.Text.RegularExpressions.Regex SlugPattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        global::System.Text.RegularExpressions.RegexOptions.NonBacktracking
        | global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsValidSlug(string slug) =>
        slug.Length <= 200 && SlugPattern.IsMatch(slug);

    /// Produce a sibling-unique slug for a new section. Returns `null` only
    /// when an explicitly-supplied slug fails the format check; an empty/
    /// missing slug always derives one from the title.
    private static async Task<string?> ResolveSectionSlugAsync(
        string? requestedSlug,
        string title,
        Guid? parentSectionId,
        IKbSectionRepository sections,
        CancellationToken ct)
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
        while (await sections.GetSectionBySlugAsync(parentSectionId, candidate, ct) is not null)
        {
            candidate = $"{baseSlug}-{suffix++}";
            if (suffix > 100) return baseSlug; // give up; let the DB UNIQUE catch the rare race
        }
        return candidate;
    }

    private static string? NormalizeIcon(string? icon)
        => string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();

    private static async Task<IResult> DeletedOk(HttpContext http, IAuditLogger audit, Guid id)
    {
        await KbAudit.WriteAsync(audit, http, "kb.section.deleted", id.ToString(), new { id });
        return Results.NoContent();
    }

    private static List<KbSectionNode> BuildTree(
        IReadOnlyList<KbSection> sections,
        Dictionary<Guid, List<KbSectionTranslation>> translationsBySection,
        string defaultLocaleCode)
    {
        // Roots and children kept in two collections so we sidestep the
        // Guid? dictionary-key nullability constraint.
        var roots = sections.Where(s => s.ParentSectionId is null).ToList();
        var byParent = sections
            .Where(s => s.ParentSectionId is not null)
            .GroupBy(s => s.ParentSectionId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<KbSectionNode> Project(Guid? parent)
        {
            List<KbSection>? children;
            if (parent is null) children = roots;
            else byParent.TryGetValue(parent.Value, out children);
            if (children is null || children.Count == 0)
                return new List<KbSectionNode>();

            return children
                .OrderBy(s => s.Position).ThenBy(s => s.Slug, StringComparer.Ordinal)
                .Select(s =>
                {
                    translationsBySection.TryGetValue(s.Id, out var ts);
                    var def = ts?.FirstOrDefault(t =>
                        string.Equals(t.LocaleCode, defaultLocaleCode, StringComparison.OrdinalIgnoreCase));
                    var fallback = ts?.FirstOrDefault();
                    var titleSource = def ?? fallback;
                    return new KbSectionNode(
                        s.Id, s.ParentSectionId, s.Slug, s.IconName, s.Position,
                        titleSource?.Title ?? s.Slug,
                        titleSource?.Description,
                        Project(s.Id));
                })
                .ToList();
        }

        return Project(null);
    }
}

public sealed record KbSectionRequest(
    Guid? ParentSectionId,
    string? Slug,
    string? IconName,
    int? Position,
    string? Title,
    string? Description,
    string? LocaleCode);

public sealed record KbSectionMoveRequest(Guid? ParentSectionId, int? Position);

public sealed record KbSectionNode(
    Guid Id,
    Guid? ParentSectionId,
    string Slug,
    string? IconName,
    int Position,
    string Title,
    string? Description,
    List<KbSectionNode> Children);
