using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Integrations.Zammad;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the v0.0.41 phase 3 dry-run engine. Routes under
/// <c>/api/admin/integrations/zammad/import</c>. Start → returns run-id
/// instantly (worker runs async). Runs-list + run-detail + records-page
/// drive the run-detail UI.
public static class ZammadDryRunEndpoints
{
    public static IEndpointRouteBuilder MapZammadDryRunEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/zammad/import")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapPost("/dry-run", StartDryRun).WithName("StartZammadDryRun").WithOpenApi();
        admin.MapPost("/import", StartImport).WithName("StartZammadImport").WithOpenApi();
        admin.MapGet("/runs", ListRuns).WithName("ListZammadImportRuns").WithOpenApi();
        admin.MapGet("/runs/{runId:guid}", GetRun).WithName("GetZammadImportRun").WithOpenApi();
        admin.MapGet("/runs/{runId:guid}/records", GetRecords).WithName("GetZammadImportRecords").WithOpenApi();
        admin.MapPost("/runs/{runId:guid}/cancel", CancelRun).WithName("CancelZammadImportRun").WithOpenApi();
        admin.MapPost("/runs/{runId:guid}/recheck", RecheckRecords).WithName("RecheckZammadImportRecords").WithOpenApi();

        return app;
    }

    public sealed record RecheckRequest(IReadOnlyList<Guid> RecordIds);

    public sealed record StartImportRequest([property: Required] Guid DryRunId);

    // ---- request shapes -------------------------------------------------

    public sealed record StartDryRunRequest(
        IReadOnlyList<long>? TicketIds,
        string? FreeText,
        IReadOnlyList<long>? GroupIds,
        IReadOnlyList<long>? StateIds,
        bool SelectAllMatching);

    // ---- handlers -------------------------------------------------------

    private static async Task<IResult> StartDryRun(
        [FromBody] StartDryRunRequest req,
        HttpContext http,
        ISettingsService settings,
        IZammadDryRunService service,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null)
        {
            return Results.BadRequest(new { error = "invalid_request", message = "Request body is required." });
        }

        var hasExplicit = req.TicketIds is { Count: > 0 };
        if (!hasExplicit && !req.SelectAllMatching)
        {
            return Results.BadRequest(new
            {
                error = "empty_selection",
                message = "Either ticketIds or selectAllMatching=true must be set.",
            });
        }

        var filter = new ZammadImportSourceFilter(
            TicketIds: hasExplicit ? req.TicketIds : null,
            FreeText: req.FreeText,
            GroupIds: req.GroupIds,
            StateIds: req.StateIds,
            SelectAllMatching: req.SelectAllMatching);

        // GetUserId throws unless the request was authenticated — the
        // RequireAdmin policy on the group guarantees it has been, so
        // the throw is defensive only.
        var userId = ActorContext.GetUserId(http);
        var runId = await service.StartDryRunAsync(filter, userId, ct);
        return Results.Accepted($"/api/admin/integrations/zammad/import/runs/{runId}", new { runId });
    }

    private static async Task<IResult> StartImport(
        [FromBody] StartImportRequest req,
        HttpContext http,
        ISettingsService settings,
        IZammadDryRunService service,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null || req.DryRunId == Guid.Empty)
        {
            return Results.BadRequest(new
            {
                error = "invalid_dry_run_id",
                message = "Provide a non-empty dryRunId.",
            });
        }

        var userId = ActorContext.GetUserId(http);
        var result = await service.StartImportFromDryRunAsync(req.DryRunId, userId, ct);
        if (result.RunId is null)
        {
            // Map service-level error codes onto HTTP status. dry-run-
            // missing → 404, anything else (state / expiry / no rows)
            // → 409 since the request is structurally valid but the
            // server state is wrong.
            var status = result.ErrorCode == "dry_run_not_found" ? 404 : 409;
            return Results.Json(new
            {
                error = result.ErrorCode ?? "import_blocked",
                message = result.ErrorMessage ?? "Import could not be started.",
            }, statusCode: status);
        }
        var runId = result.RunId.Value;
        return Results.Accepted(
            $"/api/admin/integrations/zammad/import/runs/{runId}",
            new { runId });
    }

    private static async Task<IResult> ListRuns(
        IZammadDryRunService service,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var rows = await service.GetRunsAsync(limit ?? 50, ct);
        return Results.Ok(new
        {
            items = rows.Select(MapSummary),
        });
    }

    private static async Task<IResult> GetRun(
        Guid runId,
        IZammadDryRunService service,
        CancellationToken ct)
    {
        var detail = await service.GetRunAsync(runId, ct);
        if (detail is null) return Results.NotFound(new { error = "run_not_found" });
        return Results.Ok(new
        {
            summary = MapSummary(detail.Summary),
            sourceFilter = detail.SourceFilter,
        });
    }

    private static async Task<IResult> GetRecords(
        Guid runId,
        IZammadDryRunService service,
        [FromQuery] Guid? cursor,
        [FromQuery] int? limit,
        [FromQuery(Name = "result")] string? resultFilter,
        CancellationToken ct)
    {
        var page = await service.GetRecordsAsync(runId, cursor, limit ?? 100, resultFilter, ct);
        return Results.Ok(new
        {
            items = page.Items.Select(r => new
            {
                id = r.Id,
                zammadTicketId = r.ZammadTicketId,
                zammadTicketNumber = r.ZammadTicketNumber,
                zammadTicketTitle = r.ZammadTicketTitle,
                result = r.Result,
                unresolvedReasons = r.UnresolvedReasons,
                mappingJson = r.MappingJson,
                wouldCreateTicketId = r.WouldCreateTicketId,
                createdUtc = r.CreatedUtc,
            }),
            nextCursor = page.NextCursor,
        });
    }

    private static async Task<IResult> CancelRun(
        Guid runId,
        IZammadDryRunService service,
        CancellationToken ct)
    {
        var cancelled = await service.CancelRunAsync(runId, ct);
        if (!cancelled)
        {
            return Results.Conflict(new
            {
                error = "not_cancellable",
                message = "Run is already in a terminal state (completed/failed/cancelled) or does not exist.",
            });
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RecheckRecords(
        Guid runId,
        [FromBody] RecheckRequest req,
        ISettingsService settings,
        IZammadDryRunService service,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null || req.RecordIds is null || req.RecordIds.Count == 0)
        {
            return Results.BadRequest(new { error = "empty_record_ids" });
        }
        // Defensive cap — admin UI sends a handful at a time; > 200
        // means something is off.
        if (req.RecordIds.Count > 200)
        {
            return Results.BadRequest(new { error = "too_many_record_ids", message = "Max 200 records per call." });
        }

        var n = await service.RecheckRecordsAsync(runId, req.RecordIds, ct);
        return Results.Ok(new { rechecked = n });
    }

    // ---- shared helpers -------------------------------------------------

    private static object MapSummary(ZammadImportRunSummary s) => new
    {
        id = s.Id,
        kind = s.Kind.ToString(),
        status = s.Status.ToString(),
        startedByUserId = s.StartedByUserId,
        startedByDisplayName = s.StartedByDisplayName,
        startedUtc = s.StartedUtc,
        finishedUtc = s.FinishedUtc,
        totals = new
        {
            processed = s.Totals.Processed,
            mapped = s.Totals.Mapped,
            skippedNoContact = s.Totals.SkippedNoContact,
            skippedNoGroupMapping = s.Totals.SkippedNoGroupMapping,
            skippedNoStateMapping = s.Totals.SkippedNoStateMapping,
            skippedNoPriorityMapping = s.Totals.SkippedNoPriorityMapping,
            failed = s.Totals.Failed,
            plannedTotal = s.Totals.PlannedTotal,
            imported = s.Totals.Imported,
            alreadyImported = s.Totals.AlreadyImported,
        },
        errorMessage = s.ErrorMessage,
    };

    private static async Task<IResult?> EnabledGuard(
        ISettingsService settings, CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Zammad.Enabled, ct);
        if (enabled) return null;
        return Results.Json(new
        {
            error = "integration_disabled",
            message = "Zammad integration is disabled. Toggle it on under Behaviour first.",
        }, statusCode: 409);
    }
}
