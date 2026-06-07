using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Statistics;

namespace Servicedesk.Api.Statistics;

/// Statistics feature — a light tile builder. Two surfaces under one group,
/// both Agent/Admin role-gated at the policy and feature-flag-gated per user:
///   read  (statistics_read)  → view assigned tiles, their data, save layout
///   write (statistics_write) → catalogue, CRUD on tiles, assignments
/// The flags are checked in-handler (Forbid on miss) so the gate is the real
/// security boundary, not just UI visibility.
public static class StatisticsEndpoints
{
    private static class Events
    {
        public const string Created = "statistics.tile.created";
        public const string Updated = "statistics.tile.updated";
        public const string Deleted = "statistics.tile.deleted";
        public const string Assigned = "statistics.tile.assigned";
    }

    public static IEndpointRouteBuilder MapStatisticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/statistics")
            .WithTags("Statistics")
            .RequireAuthorization("RequireAgent");

        // ---- read surface --------------------------------------------------

        group.MapGet("/tiles", async (
            HttpContext http, IUserService users, IStatisticTileService tiles, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsReadEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var rows = await tiles.GetAssignedForViewerAsync(userId.Value, ct);
            return Results.Ok(rows.Select(ToTileDto).ToList());
        }).WithName("StatisticsListAssigned").WithOpenApi();

        group.MapGet("/tiles/{id:guid}/data", async (
            Guid id, int? offset, HttpContext http, IUserService users,
            IStatisticTileService tiles, IStatisticMetricEngine engine, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();

            var canRead = await users.GetStatisticsReadEnabledAsync(userId.Value, ct);
            var canWrite = await users.GetStatisticsWriteEnabledAsync(userId.Value, ct);
            if (!canRead && !canWrite) return Results.Forbid();

            var tile = await tiles.GetByIdAsync(id, ct);
            if (tile is null) return Results.NotFound();

            // A read-only viewer may only compute tiles assigned to them; a
            // builder may preview any tile.
            if (!canWrite && !await tiles.IsTileAssignedToAsync(id, userId.Value, ct))
            {
                return Results.Forbid();
            }

            // Clamp the offset so a hostile/odd client can't ask for an
            // absurd window; ±120 units covers a decade of months.
            var clamped = Math.Clamp(offset ?? 0, -120, 0);
            var data = await engine.ComputeAsync(tile, userId.Value, clamped, ct);
            return Results.Ok(ToDataDto(data));
        }).WithName("StatisticsTileData").WithOpenApi();

        group.MapPut("/layout", async (
            [FromBody] SaveLayoutRequest request, HttpContext http,
            IUserService users, IStatisticTileService tiles, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsReadEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var layout = (request.Tiles ?? Array.Empty<LayoutEntryDto>())
                .Select(e => new StatisticLayoutEntry(e.TileId ?? "", e.Size, e.Hidden))
                .ToList();
            await tiles.SaveLayoutForViewerAsync(userId.Value, layout, ct);
            return Results.Ok();
        }).WithName("StatisticsSaveLayout").WithOpenApi();

        // ---- write surface (builder) --------------------------------------

        group.MapGet("/catalogue", async (
            HttpContext http, IUserService users, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsWriteEnabledAsync(userId.Value, ct)) return Results.Forbid();

            return Results.Ok(StatisticsCatalogue.Metrics.Select(m => new
            {
                key = m.Key,
                label = m.Label,
                unit = m.Unit,
                chartTypes = m.ChartTypes,
                groupings = m.Groupings,
                supportsScope = m.SupportsScope,
            }).ToList());
        }).WithName("StatisticsCatalogue").WithOpenApi();

        group.MapGet("/manage/tiles", async (
            HttpContext http, IUserService users, IStatisticTileService tiles, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsWriteEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var rows = await tiles.ListAllAsync(ct);
            return Results.Ok(rows.Select(ToSummaryDto).ToList());
        }).WithName("StatisticsManageList").WithOpenApi();

        group.MapGet("/manage/tiles/{id:guid}", async (
            Guid id, HttpContext http, IUserService users, IStatisticTileService tiles, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsWriteEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var tile = await tiles.GetByIdAsync(id, ct);
            if (tile is null) return Results.NotFound();
            var assignments = await tiles.GetAssignmentsAsync(id, ct);
            return Results.Ok(new
            {
                tile = ToBareTileDto(tile),
                assignedUserIds = assignments,
            });
        }).WithName("StatisticsManageGet").WithOpenApi();

        group.MapPost("/manage/tiles", async (
            [FromBody] TileInputRequest request, HttpContext http,
            IUserService users, IStatisticTileService tiles, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsWriteEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var result = await tiles.CreateAsync(request.ToInput(), userId.Value, ct);
            return result switch
            {
                SaveStatisticTileResult.Created c =>
                    await LogTileAsync(audit, http, Events.Created, c.Tile, Results.Ok(ToBareTileDto(c.Tile)), ct),
                SaveStatisticTileResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled create result."),
            };
        }).WithName("StatisticsManageCreate").WithOpenApi();

        group.MapPut("/manage/tiles/{id:guid}", async (
            Guid id, [FromBody] TileInputRequest request, HttpContext http,
            IUserService users, IStatisticTileService tiles, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsWriteEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var result = await tiles.UpdateAsync(id, request.ToInput(), userId.Value, ct);
            return result switch
            {
                SaveStatisticTileResult.Updated u =>
                    await LogTileAsync(audit, http, Events.Updated, u.Tile, Results.Ok(ToBareTileDto(u.Tile)), ct),
                SaveStatisticTileResult.NotFound => Results.NotFound(),
                SaveStatisticTileResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled update result."),
            };
        }).WithName("StatisticsManageUpdate").WithOpenApi();

        group.MapDelete("/manage/tiles/{id:guid}", async (
            Guid id, HttpContext http, IUserService users,
            IStatisticTileService tiles, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsWriteEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var deleted = await tiles.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();
            await audit.LogAsync(new AuditEvent(
                EventType: Events.Deleted,
                Actor: http.User.Identity?.Name ?? "unknown",
                ActorRole: http.User.FindFirst(ClaimTypes.Role)?.Value ?? "Agent",
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
            return Results.NoContent();
        }).WithName("StatisticsManageDelete").WithOpenApi();

        group.MapPut("/manage/tiles/{id:guid}/assignments", async (
            Guid id, [FromBody] AssignmentsRequest request, HttpContext http,
            IUserService users, IStatisticTileService tiles, IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = ResolveUserId(http);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetStatisticsWriteEnabledAsync(userId.Value, ct)) return Results.Forbid();

            var ok = await tiles.SetAssignmentsAsync(id, request.UserIds ?? Array.Empty<Guid>(), userId.Value, ct);
            if (!ok) return Results.NotFound();

            var assignments = await tiles.GetAssignmentsAsync(id, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: Events.Assigned,
                Actor: http.User.Identity?.Name ?? "unknown",
                ActorRole: http.User.FindFirst(ClaimTypes.Role)?.Value ?? "Agent",
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { assigned_user_ids = assignments }), ct);
            return Results.Ok(new { assignedUserIds = assignments });
        }).WithName("StatisticsManageAssign").WithOpenApi();

        return app;
    }

    // ---- DTO mapping ------------------------------------------------------

    private static object ToBareTileDto(StatisticTile t) => new
    {
        id = t.Id,
        title = t.Title,
        metricKey = t.MetricKey,
        chartType = t.ChartType,
        period = t.Period,
        grouping = t.Grouping,
        scope = t.Scope,
        scopeUserId = t.ScopeUserId,
        scopeUserIds = SplitCsv(t.ScopeUserIds),
    };

    private static string[] SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static object ToTileDto(StatisticTileWithLayout r) => new
    {
        id = r.Tile.Id,
        title = r.Tile.Title,
        metricKey = r.Tile.MetricKey,
        chartType = r.Tile.ChartType,
        period = r.Tile.Period,
        grouping = r.Tile.Grouping,
        scope = r.Tile.Scope,
        scopeUserId = r.Tile.ScopeUserId,
        scopeUserEmail = r.ScopeUserEmail,
        position = r.Position,
        size = r.Size,
        hidden = r.Hidden,
    };

    private static object ToSummaryDto(StatisticTileSummary s) => new
    {
        id = s.Tile.Id,
        title = s.Tile.Title,
        metricKey = s.Tile.MetricKey,
        chartType = s.Tile.ChartType,
        period = s.Tile.Period,
        grouping = s.Tile.Grouping,
        scope = s.Tile.Scope,
        scopeUserId = s.Tile.ScopeUserId,
        scopeUserEmail = s.ScopeUserEmail,
        assignedCount = s.AssignedCount,
    };

    private static object ToDataDto(StatisticTileData d) => new
    {
        tileId = d.TileId,
        metricKey = d.MetricKey,
        chartType = d.ChartType,
        unit = d.Unit,
        periodLabel = d.PeriodLabel,
        total = d.Total,
        points = d.Points.Select(p => new { label = p.Label, value = p.Value, value2 = p.Value2, segments = p.Segments }).ToList(),
        seriesLabels = d.SeriesLabels,
        generatedUtc = d.GeneratedUtc,
    };

    private static async Task<IResult> LogTileAsync(
        IAuditLogger audit, HttpContext http, string eventType, StatisticTile tile, IResult body, CancellationToken ct)
    {
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: http.User.Identity?.Name ?? "unknown",
            ActorRole: http.User.FindFirst(ClaimTypes.Role)?.Value ?? "Agent",
            Target: tile.Id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { title = tile.Title, metric = tile.MetricKey, scope = tile.Scope }), ct);
        return body;
    }

    private static Guid? ResolveUserId(HttpContext http)
    {
        var raw = http.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    // ---- request DTOs -----------------------------------------------------

    public sealed record TileInputRequest(
        string? Title, string? MetricKey, string? ChartType,
        string? Period, string? Grouping, string? Scope, Guid? ScopeUserId,
        IReadOnlyList<Guid>? ScopeUserIds)
    {
        public StatisticTileInput ToInput() => new(
            (Title ?? "").Trim(),
            MetricKey ?? "",
            ChartType ?? "",
            Period ?? "",
            Grouping ?? StatisticGroupings.None,
            Scope ?? "",
            ScopeUserId,
            ScopeUserIds);
    }

    public sealed record AssignmentsRequest(IReadOnlyList<Guid>? UserIds);
    public sealed record LayoutEntryDto(string? TileId, string? Size, bool Hidden);
    public sealed record SaveLayoutRequest(IReadOnlyList<LayoutEntryDto>? Tiles);
}
