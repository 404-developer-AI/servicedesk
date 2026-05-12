using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// Admin-only CRUD on the timesheet-task catalogue. The Tab-1 picker
/// hits the public GET below so its endpoint is intentionally
/// agent-readable, not admin-locked — agents must be able to see the
/// active catalogue to register time. Mutation routes are admin-only.
public static class TimesheetTaskEndpoints
{
    public static IEndpointRouteBuilder MapTimesheetTaskEndpoints(this IEndpointRouteBuilder app)
    {
        // Read endpoint — visible to anyone who can register time, so
        // the picker works for agents (timesheet_enabled) and managers.
        app.MapGet("/api/timesheet/tasks", async (
                bool? includeArchived,
                ITimesheetTaskService svc,
                CancellationToken ct) =>
                Results.Ok(await svc.ListAsync(includeArchived ?? false, ct)))
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .WithName("ListTimesheetTasks")
            .WithOpenApi();

        // Admin-only management surface lives under /api/admin/timesheet
        // alongside the existing /api/admin/users-style routes.
        var admin = app.MapGroup("/api/admin/timesheet/tasks")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapPost("/", async (
            [FromBody] TaskUpsertRequest req,
            HttpContext http,
            ITimesheetTaskService svc,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var result = await svc.CreateAsync(
                req.Name, req.RequiresTicket ?? true, req.IsAbsence ?? false, req.SortOrder ?? 0, ct);
            return result switch
            {
                CreateTimesheetTaskResult.Created created => await AuditAndReturn(audit, http,
                    TimesheetAudit.TaskCreated, created.Task, Results.Ok(created.Task)),
                CreateTimesheetTaskResult.NameConflict =>
                    Results.Conflict(new { error = "Another active task already uses that name." }),
                _ => Results.Problem("Unhandled create-task result."),
            };
        }).WithName("CreateTimesheetTask").WithOpenApi();

        admin.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] TaskUpsertRequest req,
            HttpContext http,
            ITimesheetTaskService svc,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var result = await svc.UpdateAsync(
                id, req.Name, req.RequiresTicket ?? true, req.IsAbsence ?? false,
                req.Archived ?? false, req.SortOrder ?? 0, ct);
            return result switch
            {
                UpdateTimesheetTaskResult.Updated updated => await AuditAndReturn(audit, http,
                    TimesheetAudit.TaskUpdated, updated.Task, Results.Ok(updated.Task)),
                UpdateTimesheetTaskResult.NotFound => Results.NotFound(),
                UpdateTimesheetTaskResult.NameConflict =>
                    Results.Conflict(new { error = "Another active task already uses that name." }),
                _ => Results.Problem("Unhandled update-task result."),
            };
        }).WithName("UpdateTimesheetTask").WithOpenApi();

        return app;
    }

    private static async Task<IResult> AuditAndReturn(
        IAuditLogger audit, HttpContext http, string eventType, TimesheetTask task, IResult body)
    {
        await TimesheetAudit.WriteAsync(audit, http, eventType, task.Id.ToString(),
            new
            {
                task.Name,
                requires_ticket = task.RequiresTicket,
                is_absence = task.IsAbsence,
                task.Archived,
                sort_order = task.SortOrder,
            });
        return body;
    }

    public sealed record TaskUpsertRequest(
        string? Name,
        bool? RequiresTicket,
        bool? IsAbsence,
        bool? Archived,
        int? SortOrder);
}
