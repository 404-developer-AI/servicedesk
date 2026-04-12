using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Persistence.Views;

namespace Servicedesk.Api.Views;

public static class ViewEndpoints
{
    public static IEndpointRouteBuilder MapViewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/views")
            .WithTags("Views")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", async (HttpContext http, IViewRepository repo, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            return Results.Ok(await repo.ListAsync(userId, ct));
        }).WithName("ListViews").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, IViewRepository repo, CancellationToken ct) =>
        {
            var view = await repo.GetAsync(id, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }).WithName("GetView").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] ViewRequest req, HttpContext http, IViewRepository repo, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var created = await repo.CreateAsync(userId, req.Name.Trim(), req.FiltersJson ?? "{}", req.SortOrder ?? 0, req.IsShared ?? false, ct);
            return Results.Created($"/api/views/{created.Id}", created);
        }).WithName("CreateView").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] ViewRequest req, IViewRepository repo, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            var updated = await repo.UpdateAsync(id, req.Name.Trim(), req.FiltersJson ?? "{}", req.SortOrder ?? 0, req.IsShared ?? false, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).WithName("UpdateView").WithOpenApi();

        group.MapDelete("/{id:guid}", async (Guid id, IViewRepository repo, CancellationToken ct) =>
        {
            var deleted = await repo.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithName("DeleteView").WithOpenApi();

        return app;
    }

    public sealed record ViewRequest(
        [property: Required] string? Name,
        string? FiltersJson,
        int? SortOrder,
        bool? IsShared);
}
