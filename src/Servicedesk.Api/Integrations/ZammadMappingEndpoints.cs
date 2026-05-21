using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Zammad;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Zammad group/state/priority → local taxonomy
/// mapping (v0.0.41 fase 3). Lives under
/// <c>/api/admin/integrations/zammad/mappings</c>. Three resource shapes,
/// each with the same {GET overview row, PUT upsert, DELETE} triple.
/// Overview is exposed as a single GET so the SPA gets a consistent
/// snapshot for all three categories + the local-target dropdowns in
/// one roundtrip.
public static class ZammadMappingEndpoints
{
    public static IEndpointRouteBuilder MapZammadMappingEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/zammad/mappings")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/overview", GetOverview).WithName("GetZammadMappingOverview").WithOpenApi();

        admin.MapPut("/groups/{zammadGroupId:long}", PutGroup).WithName("PutZammadGroupMapping").WithOpenApi();
        admin.MapDelete("/groups/{zammadGroupId:long}", DeleteGroup).WithName("DeleteZammadGroupMapping").WithOpenApi();

        admin.MapPut("/states/{zammadStateId:long}", PutState).WithName("PutZammadStateMapping").WithOpenApi();
        admin.MapDelete("/states/{zammadStateId:long}", DeleteState).WithName("DeleteZammadStateMapping").WithOpenApi();

        admin.MapPut("/priorities/{zammadPriorityId:long}", PutPriority).WithName("PutZammadPriorityMapping").WithOpenApi();
        admin.MapDelete("/priorities/{zammadPriorityId:long}", DeletePriority).WithName("DeleteZammadPriorityMapping").WithOpenApi();

        return app;
    }

    // ---- /overview ------------------------------------------------------

    private static async Task<IResult> GetOverview(
        ISettingsService settings,
        IZammadMappingService service,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        try
        {
            var overview = await service.GetOverviewAsync(ct);
            return Results.Ok(new
            {
                groups = overview.Groups.Select(r => new
                {
                    zammadId = r.ZammadId,
                    zammadName = r.ZammadName,
                    zammadActive = r.ZammadActive,
                    mappedToId = r.MappedToId,
                    mappedToName = r.MappedToName,
                }),
                states = overview.States.Select(r => new
                {
                    zammadId = r.ZammadId,
                    zammadName = r.ZammadName,
                    zammadActive = r.ZammadActive,
                    mappedToId = r.MappedToId,
                    mappedToName = r.MappedToName,
                }),
                priorities = overview.Priorities.Select(r => new
                {
                    zammadId = r.ZammadId,
                    zammadName = r.ZammadName,
                    zammadActive = r.ZammadActive,
                    mappedToId = r.MappedToId,
                    mappedToName = r.MappedToName,
                }),
                localQueues = overview.LocalQueues.Select(t => new { id = t.Id, name = t.Name, isActive = t.IsActive }),
                localStatuses = overview.LocalStatuses.Select(t => new { id = t.Id, name = t.Name, isActive = t.IsActive }),
                localPriorities = overview.LocalPriorities.Select(t => new { id = t.Id, name = t.Name, isActive = t.IsActive }),
                unmappedGroupCount = overview.UnmappedGroupCount,
                unmappedStateCount = overview.UnmappedStateCount,
                unmappedPriorityCount = overview.UnmappedPriorityCount,
            });
        }
        catch (ZammadApiException ex)
        {
            return UpstreamError(ex);
        }
    }

    // ---- mutations ------------------------------------------------------

    public sealed record PutMappingRequest(
        [property: Required] string ZammadName,
        [property: Required] Guid TargetId);

    private static async Task<IResult> PutGroup(
        long zammadGroupId,
        [FromBody] PutMappingRequest req,
        HttpContext http,
        ISettingsService settings,
        IZammadMappingService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null || string.IsNullOrWhiteSpace(req.ZammadName) || req.TargetId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "invalid_request", message = "zammadName and targetId are required." });
        }

        await service.UpsertGroupMappingAsync(zammadGroupId, req.ZammadName.Trim(), req.TargetId, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.GroupMappingUpdated,
            Actor: actor,
            ActorRole: role,
            Target: $"zammad_group:{zammadGroupId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { zammadGroupId, zammadGroupName = req.ZammadName, queueId = req.TargetId }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteGroup(
        long zammadGroupId,
        HttpContext http,
        ISettingsService settings,
        IZammadMappingService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        var deleted = await service.DeleteGroupMappingAsync(zammadGroupId, ct);
        if (!deleted) return Results.NotFound(new { error = "mapping_not_found" });

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.GroupMappingDeleted,
            Actor: actor,
            ActorRole: role,
            Target: $"zammad_group:{zammadGroupId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { zammadGroupId }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PutState(
        long zammadStateId,
        [FromBody] PutMappingRequest req,
        HttpContext http,
        ISettingsService settings,
        IZammadMappingService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null || string.IsNullOrWhiteSpace(req.ZammadName) || req.TargetId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "invalid_request", message = "zammadName and targetId are required." });
        }

        await service.UpsertStateMappingAsync(zammadStateId, req.ZammadName.Trim(), req.TargetId, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.StateMappingUpdated,
            Actor: actor,
            ActorRole: role,
            Target: $"zammad_state:{zammadStateId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { zammadStateId, zammadStateName = req.ZammadName, statusId = req.TargetId }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteState(
        long zammadStateId,
        HttpContext http,
        ISettingsService settings,
        IZammadMappingService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        var deleted = await service.DeleteStateMappingAsync(zammadStateId, ct);
        if (!deleted) return Results.NotFound(new { error = "mapping_not_found" });

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.StateMappingDeleted,
            Actor: actor,
            ActorRole: role,
            Target: $"zammad_state:{zammadStateId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { zammadStateId }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PutPriority(
        long zammadPriorityId,
        [FromBody] PutMappingRequest req,
        HttpContext http,
        ISettingsService settings,
        IZammadMappingService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;
        if (req is null || string.IsNullOrWhiteSpace(req.ZammadName) || req.TargetId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "invalid_request", message = "zammadName and targetId are required." });
        }

        await service.UpsertPriorityMappingAsync(zammadPriorityId, req.ZammadName.Trim(), req.TargetId, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.PriorityMappingUpdated,
            Actor: actor,
            ActorRole: role,
            Target: $"zammad_priority:{zammadPriorityId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { zammadPriorityId, zammadPriorityName = req.ZammadName, priorityId = req.TargetId }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeletePriority(
        long zammadPriorityId,
        HttpContext http,
        ISettingsService settings,
        IZammadMappingService service,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var blocked = await EnabledGuard(settings, ct);
        if (blocked is not null) return blocked;

        var deleted = await service.DeletePriorityMappingAsync(zammadPriorityId, ct);
        if (!deleted) return Results.NotFound(new { error = "mapping_not_found" });

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ZammadEventTypes.PriorityMappingDeleted,
            Actor: actor,
            ActorRole: role,
            Target: $"zammad_priority:{zammadPriorityId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { zammadPriorityId }), ct);
        return Results.NoContent();
    }

    // ---- shared helpers (mirrors ZammadEndpoints.cs) -------------------

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

    private static IResult UpstreamError(ZammadApiException ex) =>
        Results.Json(new
        {
            error = "upstream_error",
            httpStatus = ex.HttpStatus,
            upstreamErrorCode = ex.UpstreamErrorCode,
            message = ex.Message,
        }, statusCode: 502);
}
