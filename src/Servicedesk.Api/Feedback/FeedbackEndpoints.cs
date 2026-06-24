using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Feedback;

namespace Servicedesk.Api.Feedback;

/// Endpoints for the Employee Feedback board. Every route is gated by the
/// caller's effective feedback access (Admins always pass). The check runs
/// after the RequireAgent policy, so a customer never reaches it. Two scopes
/// (v0.0.90): FULL (<c>feedback_enabled</c>) reads/writes every row — the
/// shared board; RESTRICTED (<c>feedback_own_only</c>) may log feedback but the
/// service scopes reads/writes to the caller's own rows, and the two
/// management-only actions ("completed" toggle) reject them outright.
public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feedback")
            .WithTags("Feedback")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // Active employees for the dropdown.
        group.MapGet("/employees", async (
            HttpContext http, IUserService users, IFeedbackEntryService svc, CancellationToken ct) =>
        {
            var (_, _, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            return Results.Ok(new { items = await svc.ListEmployeesAsync(ct) });
        }).WithName("ListFeedbackEmployees").WithOpenApi();

        // Active work-point types for the dropdown (read-only; admins manage
        // the catalogue under /api/admin/feedback/work-point-types).
        group.MapGet("/work-point-types", async (
            HttpContext http, IUserService users,
            IFeedbackWorkPointTypeService types, CancellationToken ct) =>
        {
            var (_, _, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            return Results.Ok(new { items = await types.ListAsync(includeInactive: false, ct) });
        }).WithName("ListFeedbackWorkPointTypes").WithOpenApi();

        // Resolve a typed ticket number → live ticket (id + subject) so the UI
        // can render a clickable link. 404 when no live ticket carries it.
        group.MapGet("/resolve-ticket", async (
            long? number, HttpContext http, IUserService users,
            IFeedbackEntryService svc, CancellationToken ct) =>
        {
            var (_, _, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            if (number is not { } n || n <= 0)
                return Results.BadRequest(new { error = "Query parameter 'number' is required." });

            var resolution = await svc.ResolveTicketAsync(n, ct);
            return resolution.Id is null
                ? Results.NotFound(new { number = n })
                : Results.Ok(new { number = resolution.Number, id = resolution.Id, subject = resolution.Subject });
        }).WithName("ResolveFeedbackTicket").WithOpenApi();

        // Which timeline events of a ticket already have feedback logged, so the
        // ticket timeline can mark the "Log feedback" button (avoid double logs).
        group.MapGet("/logged-events", async (
            Guid? ticketId, HttpContext http, IUserService users,
            IFeedbackEntryService svc, CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            if (ticketId is not { } tid || tid == Guid.Empty)
                return Results.BadRequest(new { error = "Query parameter 'ticketId' is required." });
            return Results.Ok(new
            {
                items = await svc.ListLoggedEventsAsync(
                    tid, userId, access == FeedbackAccess.OwnOnly, ct),
            });
        }).WithName("ListFeedbackLoggedEvents").WithOpenApi();

        group.MapGet("/entries", async (
            Guid? targetUserId, Guid? workPointTypeId, bool? completed,
            HttpContext http, IUserService users, IFeedbackEntryService svc, CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;

            var filter = new FeedbackEntryFilter(targetUserId, workPointTypeId, completed);
            var rows = await svc.ListAsync(filter, userId, access == FeedbackAccess.OwnOnly, ct);
            return Results.Ok(new { items = rows });
        }).WithName("ListFeedbackEntries").WithOpenApi();

        group.MapPost("/entries", async (
            [FromBody] CreateEntryRequest? req,
            HttpContext http, IUserService users, IFeedbackEntryService svc,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (userId, _, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;

            var result = await svc.CreateAsync(userId, req?.TargetUserId, ct);
            return result switch
            {
                CreateFeedbackEntryResult.Created created => await AuditAndReturn(audit, http,
                    FeedbackAudit.EntryCreated, created.Row, Results.Ok(created.Row)),
                CreateFeedbackEntryResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled create-entry result."),
            };
        }).WithName("CreateFeedbackEntry").WithOpenApi();

        group.MapPost("/entries/log", async (
            [FromBody] LogEntryRequest req,
            HttpContext http, IUserService users, IFeedbackEntryService svc,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (userId, _, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            if (!TryBuildLogInput(req, out var input, out var error)) return error!;

            var result = await svc.LogAsync(userId, input, ct);
            return result switch
            {
                CreateFeedbackEntryResult.Created created => await AuditAndReturn(audit, http,
                    FeedbackAudit.EntryLogged, created.Row, Results.Ok(created.Row)),
                CreateFeedbackEntryResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled log-entry result."),
            };
        }).WithName("LogFeedbackEntry").WithOpenApi();

        group.MapPut("/entries/{id:guid}", async (
            Guid id, [FromBody] UpdateEntryRequest req,
            HttpContext http, IUserService users, IFeedbackEntryService svc,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            if (!TryBuildInput(req, out var input, out var error)) return error!;

            var result = await svc.UpdateAsync(id, userId, input, access == FeedbackAccess.OwnOnly, ct);
            return result switch
            {
                UpdateFeedbackEntryResult.Updated updated => await AuditAndReturn(audit, http,
                    FeedbackAudit.EntryUpdated, updated.Row, Results.Ok(updated.Row)),
                UpdateFeedbackEntryResult.NotFound => Results.NotFound(),
                UpdateFeedbackEntryResult.ValidationFailed v =>
                    Results.UnprocessableEntity(new { errors = v.Errors }),
                _ => Results.Problem("Unhandled update-entry result."),
            };
        }).WithName("UpdateFeedbackEntry").WithOpenApi();

        group.MapPost("/entries/{id:guid}/completed", async (
            Guid id, [FromBody] CompletedRequest req,
            HttpContext http, IUserService users, IFeedbackEntryService svc,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            // "Completed" is a management field — restricted users cannot set it.
            if (access == FeedbackAccess.OwnOnly) return Results.Forbid();

            var row = await svc.SetCompletedAsync(id, userId, req.Completed, ct);
            if (row is null) return Results.NotFound();

            await FeedbackAudit.WriteAsync(audit, http, FeedbackAudit.EntryCompleted, id.ToString(),
                new { is_completed = row.IsCompleted, target_user_id = row.TargetUserId });
            return Results.Ok(row);
        }).WithName("SetFeedbackEntryCompleted").WithOpenApi();

        group.MapPost("/entries/{id:guid}/mgmt-reviewed", async (
            Guid id, [FromBody] MgmtReviewedRequest req,
            HttpContext http, IUserService users, IFeedbackEntryService svc,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;
            // "Mgmt reviewed" is a management field — restricted users cannot set it.
            if (access == FeedbackAccess.OwnOnly) return Results.Forbid();

            var row = await svc.SetMgmtReviewedAsync(id, userId, req.Reviewed, ct);
            if (row is null) return Results.NotFound();

            await FeedbackAudit.WriteAsync(audit, http, FeedbackAudit.EntryMgmtReviewed, id.ToString(),
                new { mgmt_reviewed = row.MgmtReviewed, target_user_id = row.TargetUserId });
            return Results.Ok(row);
        }).WithName("SetFeedbackEntryMgmtReviewed").WithOpenApi();

        group.MapDelete("/entries/{id:guid}", async (
            Guid id, HttpContext http, IUserService users, IFeedbackEntryService svc,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (userId, access, fail) = await GateAsync(http, users, ct);
            if (fail is not null) return fail;

            var deleted = await svc.DeleteAsync(id, userId, access == FeedbackAccess.OwnOnly, ct);
            if (!deleted) return Results.NotFound();

            await FeedbackAudit.WriteAsync(audit, http, FeedbackAudit.EntryDeleted, id.ToString(), new { });
            return Results.NoContent();
        }).WithName("DeleteFeedbackEntry").WithOpenApi();

        return app;
    }

    /// Resolve the caller + their effective feedback access scope. Returns
    /// (userId, access, null) on success or (Guid.Empty, None, Unauthorized/
    /// Forbid) on miss. <see cref="FeedbackAccess.OwnOnly"/> callers reach the
    /// endpoints but the service scopes their rows; the few management-only
    /// actions reject them explicitly.
    private static async Task<(Guid UserId, FeedbackAccess Access, IResult? Fail)> GateAsync(
        HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        if (userId == Guid.Empty) return (Guid.Empty, FeedbackAccess.None, Results.Unauthorized());
        var access = (await users.GetFeedbackAccessAsync(userId, ct)).Access;
        if (access == FeedbackAccess.None) return (Guid.Empty, FeedbackAccess.None, Results.Forbid());
        return (userId, access, null);
    }

    private static async Task<IResult> AuditAndReturn(
        IAuditLogger audit, HttpContext http, string eventType, FeedbackEntryRow row, IResult body)
    {
        await FeedbackAudit.WriteAsync(audit, http, eventType, row.Id.ToString(),
            new
            {
                target_user_id = row.TargetUserId,
                entry_date = row.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                work_point_type_id = row.WorkPointTypeId,
                is_completed = row.IsCompleted,
                linked_ticket_number = row.LinkedTicketNumber,
            });
        return body;
    }

    private static bool TryBuildInput(UpdateEntryRequest req, out FeedbackEntryInput input, out IResult? error)
    {
        input = default!;
        if (req.TargetUserId is null || req.TargetUserId == Guid.Empty)
        {
            error = Results.BadRequest(new { error = "targetUserId is required." });
            return false;
        }
        if (!TryParseDate(req.EntryDate, out var entryDate))
        {
            error = Results.BadRequest(new { error = "entryDate is required as YYYY-MM-DD." });
            return false;
        }

        input = new FeedbackEntryInput(
            TargetUserId: req.TargetUserId.Value,
            EntryDate: entryDate,
            BodyHtml: req.BodyHtml ?? "",
            ManagementRemarksHtml: req.ManagementRemarksHtml ?? "",
            WorkPointTypeId: req.WorkPointTypeId,
            IsCompleted: req.IsCompleted ?? false,
            IsMgmtReviewed: req.IsMgmtReviewed ?? false,
            LinkedTicketNumber: req.LinkedTicketNumber);
        error = null;
        return true;
    }

    private static bool TryBuildLogInput(LogEntryRequest req, out LogFeedbackInput input, out IResult? error)
    {
        input = default!;
        if (req.TargetUserId is null || req.TargetUserId == Guid.Empty)
        {
            error = Results.BadRequest(new { error = "targetUserId is required." });
            return false;
        }
        if (req.LinkedTicketId is null || req.LinkedTicketId == Guid.Empty
            || req.LinkedTicketNumber is null || req.LinkedTicketEventId is null)
        {
            error = Results.BadRequest(new { error = "linkedTicketId, linkedTicketNumber and linkedTicketEventId are required." });
            return false;
        }
        // entryDate defaults to today (server) when the client omits it.
        var entryDate = TryParseDate(req.EntryDate, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.UtcNow);

        input = new LogFeedbackInput(
            TargetUserId: req.TargetUserId.Value,
            EntryDate: entryDate,
            WorkPointTypeId: req.WorkPointTypeId,
            BodyHtml: req.BodyHtml ?? "",
            LinkedTicketId: req.LinkedTicketId.Value,
            LinkedTicketNumber: req.LinkedTicketNumber.Value,
            LinkedTicketEventId: req.LinkedTicketEventId.Value);
        error = null;
        return true;
    }

    private static bool TryParseDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateOnly.TryParseExact(
            raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public sealed record CreateEntryRequest(Guid? TargetUserId);

    public sealed record UpdateEntryRequest(
        Guid? TargetUserId,
        string? EntryDate,
        string? BodyHtml,
        string? ManagementRemarksHtml,
        Guid? WorkPointTypeId,
        bool? IsCompleted,
        bool? IsMgmtReviewed,
        long? LinkedTicketNumber);

    public sealed record CompletedRequest(bool Completed);

    public sealed record MgmtReviewedRequest(bool Reviewed);

    public sealed record LogEntryRequest(
        Guid? TargetUserId,
        string? EntryDate,
        Guid? WorkPointTypeId,
        string? BodyHtml,
        Guid? LinkedTicketId,
        long? LinkedTicketNumber,
        long? LinkedTicketEventId);
}
