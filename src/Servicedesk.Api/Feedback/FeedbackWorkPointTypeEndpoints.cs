using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Feedback;

namespace Servicedesk.Api.Feedback;

/// Admin-only CRUD on the feedback work-point-type catalogue (Settings →
/// Employee Feedback). The board's picker reads the active list via the
/// agent-readable GET in <see cref="FeedbackEndpoints"/>; mutation is
/// admin-only here. The catalogue starts empty.
public static class FeedbackWorkPointTypeEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackWorkPointTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/feedback/work-point-types")
            .WithTags("Feedback")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/", async (
            bool? includeInactive, IFeedbackWorkPointTypeService svc, CancellationToken ct) =>
            Results.Ok(new { items = await svc.ListAsync(includeInactive ?? true, ct) }))
            .WithName("AdminListFeedbackWorkPointTypes").WithOpenApi();

        admin.MapPost("/", async (
            [FromBody] TypeUpsertRequest req, HttpContext http,
            IFeedbackWorkPointTypeService svc, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var result = await svc.CreateAsync(req.Name, req.Color ?? "#7c7cff", req.SortOrder ?? 0, ct);
            return result switch
            {
                CreateWorkPointTypeResult.Created created => await AuditAndReturn(audit, http,
                    FeedbackAudit.TypeCreated, created.Type, Results.Ok(created.Type)),
                CreateWorkPointTypeResult.NameConflict =>
                    Results.Conflict(new { error = "Another active type already uses that name." }),
                _ => Results.Problem("Unhandled create-type result."),
            };
        }).WithName("AdminCreateFeedbackWorkPointType").WithOpenApi();

        admin.MapPut("/{id:guid}", async (
            Guid id, [FromBody] TypeUpsertRequest req, HttpContext http,
            IFeedbackWorkPointTypeService svc, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var result = await svc.UpdateAsync(
                id, req.Name, req.Color ?? "#7c7cff", req.SortOrder ?? 0, req.IsActive ?? true, ct);
            return result switch
            {
                UpdateWorkPointTypeResult.Updated updated => await AuditAndReturn(audit, http,
                    FeedbackAudit.TypeUpdated, updated.Type, Results.Ok(updated.Type)),
                UpdateWorkPointTypeResult.NotFound => Results.NotFound(),
                UpdateWorkPointTypeResult.NameConflict =>
                    Results.Conflict(new { error = "Another active type already uses that name." }),
                _ => Results.Problem("Unhandled update-type result."),
            };
        }).WithName("AdminUpdateFeedbackWorkPointType").WithOpenApi();

        admin.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http,
            IFeedbackWorkPointTypeService svc, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await svc.DeleteAsync(id, ct);
            switch (result)
            {
                case DeleteWorkPointTypeResult.Deleted:
                    await FeedbackAudit.WriteAsync(audit, http, FeedbackAudit.TypeDeleted, id.ToString(), new { });
                    return Results.NoContent();
                case DeleteWorkPointTypeResult.InUse:
                    return Results.Conflict(new
                    {
                        error = "This type is used by existing feedback entries. Deactivate it instead.",
                    });
                default:
                    return Results.NotFound();
            }
        }).WithName("AdminDeleteFeedbackWorkPointType").WithOpenApi();

        return app;
    }

    private static async Task<IResult> AuditAndReturn(
        IAuditLogger audit, HttpContext http, string eventType, FeedbackWorkPointType type, IResult body)
    {
        await FeedbackAudit.WriteAsync(audit, http, eventType, type.Id.ToString(),
            new { type.Name, type.Color, sort_order = type.SortOrder, is_active = type.IsActive });
        return body;
    }

    public sealed record TypeUpsertRequest(string? Name, string? Color, int? SortOrder, bool? IsActive);
}
