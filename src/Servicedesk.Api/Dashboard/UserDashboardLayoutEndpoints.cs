using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Infrastructure.Dashboard;

namespace Servicedesk.Api.Dashboard;

/// User-scoped dashboard layout endpoint. Lets the signed-in user save
/// their own ordered + sized tile layout from the edit-mode UI. The
/// admin enable/disable surface lives separately on
/// `/api/admin/users/{id}/dashboard-tiles`; this endpoint never adds or
/// removes tiles from the set — every id in the payload must already be
/// enabled on the user (admin-controlled), otherwise the call is a
/// no-op for that id.
public static class UserDashboardLayoutEndpoints
{
    public static IEndpointRouteBuilder MapUserDashboardLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/dashboard")
            .WithTags("Dashboard")
            .RequireAuthorization("RequireAgent");

        group.MapPut("/layout", async (
            [FromBody] SaveDashboardLayoutRequest request,
            HttpContext httpContext,
            IDashboardTilesService tiles,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(httpContext);
            if (userId is null) return Results.Unauthorized();

            var enabled = (await tiles.GetForUserAsync(userId.Value, ct))
                .Select(t => t.TileId)
                .ToHashSet(StringComparer.Ordinal);

            // Drop any payload entries the user is not allowed to render
            // — keeps the user from sneaking unauthorized tiles into
            // their layout via the edit-mode endpoint.
            var layout = (request.Tiles ?? Array.Empty<DashboardLayoutEntry>())
                .Where(e => !string.IsNullOrWhiteSpace(e.TileId) && enabled.Contains(e.TileId.Trim()))
                .Select(e => new DashboardTilePref(e.TileId.Trim(), e.Size ?? DashboardTileSizes.Medium))
                .ToList();

            var result = await tiles.SaveLayoutForUserAsync(userId.Value, layout, ct);
            return result switch
            {
                SetDashboardTilesResult.Updated upd =>
                    Results.Ok(new { tiles = upd.Tiles.Select(t => new { tileId = t.TileId, size = t.Size }).ToList() }),
                SetDashboardTilesResult.UserNotFound => Results.NotFound(),
                SetDashboardTilesResult.CustomerNotAllowed =>
                    Results.Conflict(new { error = "Dashboard layout is only available for Agent and Admin accounts." }),
                SetDashboardTilesResult.UnknownTileIds unknown =>
                    Results.BadRequest(new { error = "Unknown tile id(s).", unknown_tile_ids = unknown.Ids }),
                SetDashboardTilesResult.InvalidSize bad =>
                    Results.BadRequest(new { error = "Invalid tile size.", size = bad.Size }),
                _ => Results.Problem("Unhandled dashboard layout result."),
            };
        }).WithName("UserDashboardLayoutSave").WithOpenApi();

        return app;
    }

    private static Guid? ResolveUserId(HttpContext httpContext)
    {
        var raw = httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

public sealed record DashboardLayoutEntry(string TileId, string? Size);
public sealed record SaveDashboardLayoutRequest(IReadOnlyList<DashboardLayoutEntry>? Tiles);
