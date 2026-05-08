using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.KnowledgeBase;
using Servicedesk.Infrastructure.Audit;
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
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

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
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        adminGroup.MapPost("/sections", async (
            [FromBody] KbSectionRequest req, HttpContext http,
            IKbConfigRepository configs, IKbSectionRepository sections,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Slug))
                return Results.BadRequest(new { error = "Slug is required." });
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required." });

            var slug = req.Slug.Trim().ToLowerInvariant();
            if (!IsValidSlug(slug))
                return Results.BadRequest(new { error = "Slug must be lowercase ASCII letters/digits separated by single hyphens." });

            // Sibling-slug clash → 409 to mirror DB constraint behaviour.
            if (await sections.GetSectionBySlugAsync(req.ParentSectionId, slug, ct) is not null)
                return Results.Conflict(new { error = "A sibling section already uses this slug." });

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

            if (string.IsNullOrWhiteSpace(req.Slug))
                return Results.BadRequest(new { error = "Slug is required." });
            var slug = req.Slug.Trim().ToLowerInvariant();
            if (!IsValidSlug(slug))
                return Results.BadRequest(new { error = "Slug must be lowercase ASCII letters/digits separated by single hyphens." });

            if (!string.Equals(slug, existing.Slug, StringComparison.OrdinalIgnoreCase))
            {
                var clash = await sections.GetSectionBySlugAsync(existing.ParentSectionId, slug, ct);
                if (clash is not null && clash.Id != id)
                    return Results.Conflict(new { error = "A sibling section already uses this slug." });
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

    private static bool IsValidSlug(string slug) =>
        global::System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9]+(-[a-z0-9]+)*$");

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
