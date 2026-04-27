using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Triggers;

namespace Servicedesk.Api.Triggers;

/// v0.0.24 Blok 6 — admin CRUD for triggers. Every route is admin-only;
/// the evaluator + scheduler are infra services and never accept user
/// input directly. Mutating endpoints write a single audit row each so
/// the append-only chain captures who changed what — value beyond the
/// regular HTTP access log because the trigger payload itself is the
/// security-relevant artefact.
public static class TriggerEndpoints
{
    private const int RunSummaryWindowHours = 24;

    public static IEndpointRouteBuilder MapTriggerEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/triggers")
            .WithTags("Triggers")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/", async (ITriggerRepository repo, CancellationToken ct) =>
        {
            var triggers = await repo.ListAllAsync(ct);
            var since = DateTime.UtcNow.AddHours(-RunSummaryWindowHours);
            var summaries = await repo.GetRunSummariesAsync(since, ct);
            var items = triggers.Select(t => ProjectListItem(t, summaries)).ToArray();
            return Results.Ok(new { items, runSummaryWindowHours = RunSummaryWindowHours });
        }).WithName("ListTriggers").WithOpenApi();

        admin.MapGet("/{id:guid}", async (Guid id, ITriggerRepository repo, CancellationToken ct) =>
        {
            var row = await repo.GetByIdAsync(id, ct);
            return row is null ? Results.NotFound() : Results.Ok(ProjectDetail(row));
        }).WithName("GetTrigger").WithOpenApi();

        admin.MapPost("/", async (
            [FromBody] TriggerInput req, HttpContext http, ITriggerRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var validation = ValidateAndNormalize(req, out var normalized);
            if (!validation.IsValid) return Results.BadRequest(new { error = validation.Error });

            // Self-chain via nextTriggerId is impossible on POST (no self-id
            // exists yet) — pass null so the validator falls through to the
            // straight DB-lookup path.
            var chainCheck = await TriggerValidator.ValidateChainTargetsAsync(
                normalized.ActionsJson, normalized.ActivatorKind, normalized.ActivatorMode,
                selfId: null, repo, ct);
            if (!chainCheck.IsValid) return Results.BadRequest(new { error = chainCheck.Error });

            var creatorId = TryGetUserId(http);
            var row = await repo.CreateAsync(new NewTrigger(
                Name: normalized.Name,
                Description: normalized.Description,
                IsActive: normalized.IsActive,
                ActivatorKind: normalized.ActivatorKind,
                ActivatorMode: normalized.ActivatorMode,
                ConditionsJson: normalized.ConditionsJson,
                ActionsJson: normalized.ActionsJson,
                Locale: normalized.Locale,
                Timezone: normalized.Timezone,
                Note: normalized.Note,
                CreatedByUserId: creatorId), ct);

            await LogAsync(audit, http, TriggerAuditEventTypes.Created, row.Id.ToString(), new
            {
                row.Name, row.IsActive, row.ActivatorKind, row.ActivatorMode,
            });

            return Results.Created($"/api/admin/triggers/{row.Id}", ProjectDetail(row));
        }).WithName("CreateTrigger").WithOpenApi();

        admin.MapPut("/{id:guid}", async (
            Guid id, [FromBody] TriggerInput req, HttpContext http, ITriggerRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var validation = ValidateAndNormalize(req, out var normalized);
            if (!validation.IsValid) return Results.BadRequest(new { error = validation.Error });

            // Pass the row's own id as selfId so a re-arm self-chain
            // ("trigger A pending-tills back to A") doesn't trigger the
            // not-found path on its own row.
            var chainCheck = await TriggerValidator.ValidateChainTargetsAsync(
                normalized.ActionsJson, normalized.ActivatorKind, normalized.ActivatorMode,
                selfId: id, repo, ct);
            if (!chainCheck.IsValid) return Results.BadRequest(new { error = chainCheck.Error });

            var row = await repo.UpdateAsync(id, new UpdateTrigger(
                Name: normalized.Name,
                Description: normalized.Description,
                IsActive: normalized.IsActive,
                ActivatorKind: normalized.ActivatorKind,
                ActivatorMode: normalized.ActivatorMode,
                ConditionsJson: normalized.ConditionsJson,
                ActionsJson: normalized.ActionsJson,
                Locale: normalized.Locale,
                Timezone: normalized.Timezone,
                Note: normalized.Note), ct);
            if (row is null) return Results.NotFound();

            await LogAsync(audit, http, TriggerAuditEventTypes.Updated, row.Id.ToString(), new
            {
                row.Name, row.IsActive, row.ActivatorKind, row.ActivatorMode,
            });

            return Results.Ok(ProjectDetail(row));
        }).WithName("UpdateTrigger").WithOpenApi();

        // Toggle is its own endpoint so the list-row switch can flip
        // active state without re-submitting the full payload (avoids
        // accidentally clobbering a draft someone else has open).
        admin.MapPost("/{id:guid}/active", async (
            Guid id, [FromBody] ActiveToggleInput req, HttpContext http, ITriggerRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var ok = await repo.SetActiveAsync(id, req.IsActive, ct);
            if (!ok) return Results.NotFound();
            await LogAsync(audit, http, TriggerAuditEventTypes.Updated, id.ToString(), new
            {
                action = "set_active",
                isActive = req.IsActive,
            });
            return Results.NoContent();
        }).WithName("SetTriggerActive").WithOpenApi();

        admin.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ITriggerRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var ok = await repo.DeleteAsync(id, ct);
            if (!ok) return Results.NotFound();
            await LogAsync(audit, http, TriggerAuditEventTypes.Deleted, id.ToString(), null);
            return Results.NoContent();
        }).WithName("DeleteTrigger").WithOpenApi();

        // Static metadata for the UI: lists every condition-field key,
        // operator, and action-kind the server understands. Lets the
        // editor build pickers without hardcoding the same lists in TS.
        admin.MapGet("/metadata", () => Results.Ok(new
        {
            conditionFields = TriggerConditionFieldCatalog.All,
            conditionOperators = TriggerValidator.ConditionOperators.OrderBy(o => o, StringComparer.Ordinal).ToArray(),
            actionKinds = TriggerValidator.ActionKinds.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            activatorPairs = TriggerValidator.ActivatorPairs.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            templateVariables = TriggerTemplateVariableCatalog.All,
            maxConditionDepth = TriggerValidator.MaxConditionDepth,
        })).WithName("GetTriggerMetadata").WithOpenApi();

        // Blok 7 — admin test-runner. Evaluates the trigger against a
        // real ticket without committing anything. Per-action results
        // mirror the runtime outcome shape so the UI can re-use the
        // same diff-rendering it already has for run-history.
        admin.MapPost("/{id:guid}/dry-run", async (
            Guid id, [FromBody] DryRunInput req, ITriggerService service, CancellationToken ct) =>
        {
            if (req.TicketId == Guid.Empty)
                return Results.BadRequest(new { error = "ticketId is required." });

            var result = await service.DryRunAsync(id, req.TicketId, ct);
            if (result is null) return Results.NotFound();

            return Results.Ok(new
            {
                matched = result.Matched,
                failureReason = result.FailureReason,
                actions = result.Actions.Select(a => new
                {
                    kind = a.Kind,
                    status = a.Status.ToString().ToLowerInvariant(),
                    summary = a.Summary,
                    failure = a.FailureReason,
                }),
            });
        }).WithName("DryRunTrigger").WithOpenApi();

        admin.MapGet("/{id:guid}/runs", async (
            Guid id, int? limit, DateTime? cursorUtc, ITriggerRepository repo, CancellationToken ct) =>
        {
            var trigger = await repo.GetByIdAsync(id, ct);
            if (trigger is null) return Results.NotFound();
            var clamped = Math.Clamp(limit ?? 50, 1, 200);
            var rows = await repo.ListRunsAsync(id, clamped, cursorUtc, ct);
            DateTime? next = rows.Count == clamped ? rows[^1].FiredUtc : null;
            return Results.Ok(new
            {
                items = rows.Select(r => new
                {
                    id = r.Id,
                    triggerId = r.TriggerId,
                    ticketId = r.TicketId,
                    ticketNumber = r.TicketNumber,
                    ticketEventId = r.TicketEventId,
                    firedUtc = r.FiredUtc,
                    outcome = r.Outcome,
                    appliedChangesJson = r.AppliedChangesJson,
                    errorClass = r.ErrorClass,
                    errorMessage = r.ErrorMessage,
                }),
                nextCursor = next,
            });
        }).WithName("ListTriggerRuns").WithOpenApi();

        return app;
    }

    private static object ProjectListItem(TriggerRow row, IReadOnlyDictionary<Guid, TriggerRunSummary> summaries)
    {
        summaries.TryGetValue(row.Id, out var summary);
        return new
        {
            id = row.Id,
            name = row.Name,
            description = row.Description,
            isActive = row.IsActive,
            activatorKind = row.ActivatorKind,
            activatorMode = row.ActivatorMode,
            locale = row.Locale,
            timezone = row.Timezone,
            createdUtc = row.CreatedUtc,
            updatedUtc = row.UpdatedUtc,
            runs = new
            {
                applied = summary?.AppliedCount ?? 0,
                skippedNoMatch = summary?.SkippedNoMatchCount ?? 0,
                skippedLoop = summary?.SkippedLoopCount ?? 0,
                failed = summary?.FailedCount ?? 0,
                lastFiredUtc = summary?.LastFiredUtc,
            },
        };
    }

    private static object ProjectDetail(TriggerRow row) => new
    {
        id = row.Id,
        name = row.Name,
        description = row.Description,
        isActive = row.IsActive,
        activatorKind = row.ActivatorKind,
        activatorMode = row.ActivatorMode,
        conditionsJson = row.ConditionsJson,
        actionsJson = row.ActionsJson,
        locale = row.Locale,
        timezone = row.Timezone,
        note = row.Note,
        createdUtc = row.CreatedUtc,
        updatedUtc = row.UpdatedUtc,
        createdByUserId = row.CreatedByUserId,
    };

    private static Servicedesk.Infrastructure.Triggers.ValidationResult ValidateAndNormalize(TriggerInput req, out NormalizedInput normalized)
    {
        normalized = new NormalizedInput(
            Name: (req.Name ?? string.Empty).Trim(),
            Description: req.Description ?? string.Empty,
            IsActive: req.IsActive ?? true,
            ActivatorKind: (req.ActivatorKind ?? string.Empty).Trim().ToLowerInvariant(),
            ActivatorMode: (req.ActivatorMode ?? string.Empty).Trim().ToLowerInvariant(),
            ConditionsJson: string.IsNullOrWhiteSpace(req.ConditionsJson)
                ? "{\"op\":\"AND\",\"items\":[]}"
                : req.ConditionsJson,
            ActionsJson: string.IsNullOrWhiteSpace(req.ActionsJson) ? "[]" : req.ActionsJson,
            Locale: string.IsNullOrWhiteSpace(req.Locale) ? null : req.Locale!.Trim(),
            Timezone: string.IsNullOrWhiteSpace(req.Timezone) ? null : req.Timezone!.Trim(),
            Note: req.Note ?? string.Empty);

        return TriggerValidator.Validate(
            normalized.Name,
            normalized.ActivatorKind,
            normalized.ActivatorMode,
            normalized.ConditionsJson,
            normalized.ActionsJson,
            normalized.Locale,
            normalized.Timezone);
    }

    private static Guid? TryGetUserId(HttpContext http)
    {
        var raw = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static async Task LogAsync(IAuditLogger audit, HttpContext http, string eventType, string? target, object? payload)
    {
        var actor = http.User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown";
        var role = http.User.FindFirst(ClaimTypes.Role)?.Value ?? "unknown";
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target,
            Payload: payload ?? new { }));
    }

    public sealed record TriggerInput(
        [property: Required] string? Name,
        string? Description,
        bool? IsActive,
        [property: Required] string? ActivatorKind,
        [property: Required] string? ActivatorMode,
        string? ConditionsJson,
        string? ActionsJson,
        string? Locale,
        string? Timezone,
        string? Note);

    public sealed record ActiveToggleInput([property: Required] bool IsActive);

    public sealed record DryRunInput([property: Required] Guid TicketId);

    private sealed record NormalizedInput(
        string Name,
        string Description,
        bool IsActive,
        string ActivatorKind,
        string ActivatorMode,
        string ConditionsJson,
        string ActionsJson,
        string? Locale,
        string? Timezone,
        string Note);
}
