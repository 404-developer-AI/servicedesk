using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Zammad;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the v0.0.43 Knowledge Base import. All routes under
/// <c>/api/admin/integrations/zammad/kb-import</c>, admin-only.
///
/// Stepper-friendly: each endpoint corresponds to one stage of the UI
/// flow and is idempotent so the SPA can poll/refresh safely.
public static class ZammadKbImportEndpoints
{
    public static IEndpointRouteBuilder MapZammadKbImportEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/zammad/kb-import")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/knowledge-bases", ListKnowledgeBases)
            .WithName("ListZammadKnowledgeBases").WithOpenApi();

        admin.MapPost("/runs", StartRun)
            .WithName("StartZammadKbImportRun").WithOpenApi();

        admin.MapGet("/runs", ListRuns)
            .WithName("ListZammadKbImportRuns").WithOpenApi();

        admin.MapGet("/runs/{runId:guid}", GetRun)
            .WithName("GetZammadKbImportRun").WithOpenApi();

        admin.MapPost("/runs/{runId:guid}/proposal", BuildProposal)
            .WithName("BuildZammadKbImportProposal").WithOpenApi();

        admin.MapGet("/runs/{runId:guid}/proposal", GetProposal)
            .WithName("GetZammadKbImportProposal").WithOpenApi();

        admin.MapPost("/runs/{runId:guid}/proposal/decisions", SaveDecisions)
            .WithName("SaveZammadKbImportDecisions").WithOpenApi();

        admin.MapPost("/runs/{runId:guid}/proposal/apply", ApplyProposal)
            .WithName("ApplyZammadKbImportProposal").WithOpenApi();

        admin.MapGet("/runs/{runId:guid}/picker", PickerList)
            .WithName("ListZammadKbImportPicker").WithOpenApi();

        admin.MapPost("/runs/{runId:guid}/import", StartArticleImport)
            .WithName("StartZammadKbArticleImport").WithOpenApi();

        admin.MapGet("/runs/{runId:guid}/records", ListRecords)
            .WithName("ListZammadKbImportRecords").WithOpenApi();

        admin.MapPost("/runs/{runId:guid}/cancel", CancelRun)
            .WithName("CancelZammadKbImportRun").WithOpenApi();

        return app;
    }

    // ---- request shapes ---------------------------------------------

    public sealed record BuildProposalRequest(long KnowledgeBaseId);
    public sealed record SaveDecisionsRequest(IReadOnlyList<ZammadKbProposalNode> Nodes);
    public sealed record StartImportRequest(IReadOnlyList<long> AnswerIds);

    // ---- handlers ---------------------------------------------------

    private static async Task<IResult> ListKnowledgeBases(
        ISettingsService settings,
        IZammadKbImportService service,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        try
        {
            var items = await service.ListKnowledgeBasesAsync(ct);
            return Results.Ok(new { items });
        }
        catch (ZammadApiException ex)
        {
            return MapApiError(ex);
        }
    }

    private static async Task<IResult> StartRun(
        HttpContext http,
        ISettingsService settings,
        IZammadKbImportService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        var userId = ActorContext.GetUserId(http);
        var runId = await service.StartRunAsync(userId, ct);
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.KbImportStarted,
            Actor: actor,
            ActorRole: role,
            Target: runId.ToString(),
            Payload: new { runId }), ct);
        return Results.Accepted($"/api/admin/integrations/zammad/kb-import/runs/{runId}", new { runId });
    }

    private static async Task<IResult> ListRuns(
        IZammadKbImportService service,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var rows = await service.ListRunsAsync(limit ?? 50, ct);
        return Results.Ok(new { items = rows.Select(MapSummary) });
    }

    private static async Task<IResult> GetRun(
        Guid runId,
        IZammadKbImportService service,
        CancellationToken ct)
    {
        var summary = await service.GetRunAsync(runId, ct);
        if (summary is null) return Results.NotFound(new { error = "run_not_found" });
        return Results.Ok(new { summary = MapSummary(summary) });
    }

    private static async Task<IResult> BuildProposal(
        Guid runId,
        [FromBody] BuildProposalRequest req,
        ISettingsService settings,
        IZammadKbImportService service,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null || req.KnowledgeBaseId <= 0)
        {
            return Results.BadRequest(new
            {
                error = "invalid_knowledge_base_id",
                message = "Provide a positive knowledgeBaseId.",
            });
        }
        try
        {
            var proposal = await service.BuildProposalAsync(runId, req.KnowledgeBaseId, ct);
            if (proposal is null) return Results.NotFound(new { error = "run_not_found" });
            return Results.Ok(proposal);
        }
        catch (ZammadApiException ex)
        {
            return MapApiError(ex);
        }
    }

    private static async Task<IResult> GetProposal(
        Guid runId,
        IZammadKbImportService service,
        CancellationToken ct)
    {
        var proposal = await service.GetProposalAsync(runId, ct);
        if (proposal is null) return Results.NotFound(new { error = "no_proposal_yet" });
        return Results.Ok(proposal);
    }

    private static async Task<IResult> SaveDecisions(
        Guid runId,
        [FromBody] SaveDecisionsRequest req,
        IZammadKbImportService service,
        CancellationToken ct)
    {
        if (req is null || req.Nodes is null || req.Nodes.Count == 0)
        {
            return Results.BadRequest(new { error = "empty_nodes" });
        }
        var ok = await service.SaveSectionDecisionsAsync(runId, req.Nodes, ct);
        if (!ok)
        {
            return Results.Conflict(new
            {
                error = "not_in_awaiting_approval",
                message = "Decisions can only be saved while the run is in awaiting_approval.",
            });
        }
        return Results.NoContent();
    }

    private static async Task<IResult> ApplyProposal(
        Guid runId,
        HttpContext http,
        IZammadKbImportService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        var n = await service.ApplySectionsAsync(runId, userId, ct);
        if (n == 0)
        {
            return Results.Conflict(new
            {
                error = "apply_blocked",
                message = "Proposal is empty or run is not in awaiting_approval.",
            });
        }
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.KbSectionsApproved,
            Actor: actor,
            ActorRole: role,
            Target: runId.ToString(),
            Payload: new { runId, mappingCount = n }), ct);
        return Results.Ok(new { mappingCount = n });
    }

    private static async Task<IResult> PickerList(
        Guid runId,
        [FromQuery] string? status,
        [FromQuery] long? categoryId,
        [FromQuery] string? freeText,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ISettingsService settings,
        IZammadKbImportService service,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        try
        {
            var p = await service.ListPickerAsync(runId, status, categoryId, freeText,
                page ?? 1, pageSize ?? 50, ct);
            return Results.Ok(new { items = p.Items, total = p.Total });
        }
        catch (ZammadApiException ex)
        {
            return MapApiError(ex);
        }
    }

    private static async Task<IResult> StartArticleImport(
        Guid runId,
        [FromBody] StartImportRequest req,
        HttpContext http,
        ISettingsService settings,
        IZammadKbImportService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null || req.AnswerIds is null || req.AnswerIds.Count == 0)
        {
            return Results.BadRequest(new { error = "empty_selection" });
        }
        if (req.AnswerIds.Count > 5_000)
        {
            return Results.BadRequest(new
            {
                error = "selection_too_large",
                message = "Up to 5000 answers per run.",
            });
        }
        var userId = ActorContext.GetUserId(http);
        var ok = await service.StartArticleImportAsync(runId, req.AnswerIds, userId, ct);
        if (!ok)
        {
            return Results.Conflict(new
            {
                error = "start_blocked",
                message = "Run is not in 'approved' status; complete the section approval first.",
            });
        }
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.KbImportStarted,
            Actor: actor,
            ActorRole: role,
            Target: runId.ToString(),
            Payload: new { runId, answerCount = req.AnswerIds.Count }), ct);
        return Results.Accepted($"/api/admin/integrations/zammad/kb-import/runs/{runId}");
    }

    private static async Task<IResult> ListRecords(
        Guid runId,
        IZammadKbImportService service,
        [FromQuery] Guid? cursor,
        [FromQuery] int? limit,
        [FromQuery(Name = "result")] string? resultFilter,
        CancellationToken ct)
    {
        var page = await service.ListRecordsAsync(runId, cursor, limit ?? 100, resultFilter, ct);
        return Results.Ok(new
        {
            items = page.Items.Select(r => new
            {
                id = r.Id,
                zammadAnswerId = r.ZammadAnswerId,
                zammadCategoryId = r.ZammadCategoryId,
                zammadTitle = r.ZammadTitle,
                result = r.Result,
                unresolvedReasons = r.UnresolvedReasons,
                mappingJson = r.MappingJson,
                targetArticleId = r.TargetArticleId,
                createdUtc = r.CreatedUtc,
            }),
            nextCursor = page.NextCursor,
        });
    }

    private static async Task<IResult> CancelRun(
        Guid runId,
        HttpContext http,
        IZammadKbImportService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var ok = await service.CancelRunAsync(runId, ct);
        if (!ok)
        {
            return Results.Conflict(new
            {
                error = "not_cancellable",
                message = "Run is already terminal or does not exist.",
            });
        }
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.KbImportCancelled,
            Actor: actor,
            ActorRole: role,
            Target: runId.ToString(),
            Payload: new { runId }), ct);
        return Results.NoContent();
    }

    // ---- helpers ------------------------------------------------------

    private static object MapSummary(ZammadKbImportRunSummary s) => new
    {
        id = s.Id,
        status = s.Status.ToString(),
        startedByUserId = s.StartedByUserId,
        startedByDisplayName = s.StartedByDisplayName,
        startedUtc = s.StartedUtc,
        finishedUtc = s.FinishedUtc,
        sourceKbId = s.SourceKbId,
        sourceKbName = s.SourceKbName,
        totals = new
        {
            plannedTotal = s.Totals.PlannedTotal,
            processed = s.Totals.Processed,
            imported = s.Totals.Imported,
            alreadyImported = s.Totals.AlreadyImported,
            skippedNoSectionMapping = s.Totals.SkippedNoSectionMapping,
            skippedNoTranslation = s.Totals.SkippedNoTranslation,
            skippedSectionSkipped = s.Totals.SkippedSectionSkipped,
            failed = s.Totals.Failed,
        },
        errorMessage = s.ErrorMessage,
    };

    private static async Task<IResult?> EnabledGuard(ISettingsService settings, CancellationToken ct)
    {
        var enabled = await settings.GetAsync<bool>(SettingKeys.Zammad.Enabled, ct);
        if (enabled) return null;
        return Results.Json(new
        {
            error = "integration_disabled",
            message = "Zammad integration is disabled. Toggle it on under Behaviour first.",
        }, statusCode: 409);
    }

    private static IResult MapApiError(ZammadApiException ex)
    {
        var status = ex.HttpStatus switch
        {
            401 => 401,
            403 => 403,
            404 => 404,
            _ => 502,
        };
        return Results.Json(new
        {
            error = ex.UpstreamErrorCode ?? "zammad_api_error",
            message = ex.Message,
        }, statusCode: status);
    }
}
