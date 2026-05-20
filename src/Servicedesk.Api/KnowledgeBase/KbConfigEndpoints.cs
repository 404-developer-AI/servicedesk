using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.KnowledgeBase;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;

namespace Servicedesk.Api.KnowledgeBase;

/// Admin-only management of the KB-level config row (active toggle +
/// default locale) and the supported-locales table. The config row itself
/// is a singleton seeded by <c>DatabaseBootstrapper</c>; this endpoint just
/// flips its fields.
public static class KbConfigEndpoints
{
    public static IEndpointRouteBuilder MapKbConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/kb")
            .WithTags("KnowledgeBase")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .RequireKbAccess();

        group.MapGet("/config", async (IKbConfigRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetConfigAsync(ct)))
            .WithName("GetKbConfig").WithOpenApi();

        group.MapPut("/config", async (
            [FromBody] KbConfigRequest req, HttpContext http,
            IKbConfigRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.DefaultLocaleCode))
                return Results.BadRequest(new { error = "DefaultLocaleCode is required." });
            var locale = await repo.GetLocaleAsync(req.DefaultLocaleCode!, ct);
            if (locale is null)
                return Results.BadRequest(new { error = $"Unknown locale '{req.DefaultLocaleCode}'." });

            var updated = await repo.UpdateConfigAsync(req.IsActive ?? true, req.DefaultLocaleCode!, ct);
            await KbAudit.WriteAsync(audit, http, "kb.config.updated", updated.Id.ToString(),
                new { updated.IsActive, updated.DefaultLocaleCode });
            return Results.Ok(updated);
        }).WithName("UpdateKbConfig").WithOpenApi();

        group.MapGet("/locales", async (
            bool? includeInactive, IKbConfigRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListLocalesAsync(includeInactive ?? true, ct)))
            .WithName("ListKbLocales").WithOpenApi();

        group.MapPost("/locales", async (
            [FromBody] KbLocaleRequest req, HttpContext http,
            IKbConfigRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateLocale(req) is { } err) return err;
            var saved = await repo.UpsertLocaleAsync(new KbLocale(
                Code: req.Code!.Trim(),
                DisplayName: req.DisplayName!.Trim(),
                IsActive: req.IsActive ?? true,
                SortOrder: req.SortOrder ?? 0), ct);
            await KbAudit.WriteAsync(audit, http, "kb.locale.upserted", saved.Code, new { saved.Code, saved.IsActive });
            return Results.Ok(saved);
        }).WithName("UpsertKbLocale").WithOpenApi();

        group.MapDelete("/locales/{code}", async (
            string code, HttpContext http,
            IKbConfigRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var config = await repo.GetConfigAsync(ct);
            if (string.Equals(code, config.DefaultLocaleCode, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "Cannot remove the default locale. Change the default first." });

            if (await repo.LocaleHasTranslationsAsync(code, ct))
                return Results.Conflict(new { error = "Locale still has translations. Remove or move them first." });

            var removed = await repo.RemoveLocaleAsync(code, ct);
            if (!removed) return Results.NotFound();
            await KbAudit.WriteAsync(audit, http, "kb.locale.removed", code, new { code });
            return Results.NoContent();
        }).WithName("RemoveKbLocale").WithOpenApi();

        return app;
    }

    private static IResult? ValidateLocale(KbLocaleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return Results.BadRequest(new { error = "Code is required." });
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return Results.BadRequest(new { error = "DisplayName is required." });
        return null;
    }
}

public sealed record KbConfigRequest(bool? IsActive, string? DefaultLocaleCode);

public sealed record KbLocaleRequest(string? Code, string? DisplayName, bool? IsActive, int? SortOrder);
