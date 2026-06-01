using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Timesheet;

namespace Servicedesk.Api.Timesheet;

/// Migration import for historical timesheet data (v0.0.54).
///
/// Two surfaces with two different auth models:
///
/// 1. Admin config — <c>/api/admin/timesheet/import</c>, session + admin.
///    Toggles the master switch and manages the pre-shared import secret.
///    Mirrors the Telavox /secret pattern: only existence is ever reported,
///    never the value.
///
/// 2. Migration surface — <c>/api/timesheet/import</c>, secret-gated (no
///    session). The standalone migration tool reads the target task/user
///    catalogues to build its mapping file, then pushes normalised rows.
///    The surface is invisible (404) unless the admin both enabled it AND
///    configured the secret; a present-but-wrong token gets 401.
///
/// Import bypasses the interactive entry validation on purpose (see
/// <see cref="ITimesheetImportService"/>); every privileged action here is
/// audited, and batches are upserted idempotently on (source, ref).
public static class TimesheetImportEndpoints
{
    private const string TokenHeader = "X-Timesheet-Import-Token";

    /// Bounds the per-request payload so a malformed or hostile caller
    /// can't push an unbounded array into memory. The migration tool sends
    /// ~1000-row batches.
    private const int MaxRowsPerBatch = 5000;

    public static IEndpointRouteBuilder MapTimesheetImportEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Admin config (session + admin) --------------------------------
        var admin = app.MapGroup("/api/admin/timesheet/import")
            .WithTags("Timesheet")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", async (
            ISettingsService settings, IProtectedSecretStore secrets, CancellationToken ct) =>
        {
            var enabled = await settings.GetAsync<bool>(SettingKeys.Timesheet.ImportEnabled, ct);
            var configured = await secrets.HasAsync(ProtectedSecretKeys.TimesheetImportToken, ct);
            return Results.Ok(new { enabled, secretConfigured = configured });
        }).WithName("GetTimesheetImportStatus").WithOpenApi();

        admin.MapPut("/enabled", async (
            [FromBody] SetEnabledRequest req,
            HttpContext http, ISettingsService settings, IAuditLogger audit, CancellationToken ct) =>
        {
            var (actor, role) = ActorContext.Resolve(http);
            await settings.SetAsync(SettingKeys.Timesheet.ImportEnabled, req.Enabled, actor, role, ct);
            await TimesheetAudit.WriteAsync(audit, http, TimesheetAudit.ImportEnabledChanged,
                SettingKeys.Timesheet.ImportEnabled, new { enabled = req.Enabled });
            return Results.Ok(new { enabled = req.Enabled });
        }).WithName("SetTimesheetImportEnabled").WithOpenApi();

        admin.MapPut("/secret", async (
            [FromBody] SetSecretRequest req,
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            var value = (req?.Value ?? string.Empty).Trim();
            // A short token is a foot-gun on an internet-facing surface;
            // require real entropy. The tool generates a long random token.
            if (value.Length < 24 || value.Length > 256)
                return Results.BadRequest(new { error = "invalid_secret", message = "Import token must be 24–256 characters." });
            await secrets.SetAsync(ProtectedSecretKeys.TimesheetImportToken, value, ct);
            await TimesheetAudit.WriteAsync(audit, http, TimesheetAudit.ImportSecretUpdated,
                ProtectedSecretKeys.TimesheetImportToken, new { configured = true });
            return Results.NoContent();
        }).WithName("SetTimesheetImportSecret").WithOpenApi();

        admin.MapDelete("/secret", async (
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            await secrets.DeleteAsync(ProtectedSecretKeys.TimesheetImportToken, ct);
            await TimesheetAudit.WriteAsync(audit, http, TimesheetAudit.ImportSecretDeleted,
                ProtectedSecretKeys.TimesheetImportToken, new { configured = false });
            return Results.NoContent();
        }).WithName("DeleteTimesheetImportSecret").WithOpenApi();

        // ---- Migration surface (secret-gated) ------------------------------
        var mig = app.MapGroup("/api/timesheet/import").WithTags("Timesheet");

        mig.MapGet("/tasks", async (
            HttpContext http, ISettingsService settings, IProtectedSecretStore secrets,
            ITimesheetImportService svc, CancellationToken ct) =>
        {
            var deny = await CheckImportAuthAsync(http, settings, secrets, ct);
            if (deny is not null) return deny;

            var tasks = await svc.ListTasksAsync(ct);
            return Results.Ok(new
            {
                items = tasks.Select(t => new
                {
                    id = t.Id,
                    name = t.Name,
                    requiresTicket = t.RequiresTicket,
                    isAbsence = t.IsAbsence,
                    archived = t.Archived,
                }),
            });
        }).WithName("ListTimesheetImportTasks").WithOpenApi();

        mig.MapGet("/users", async (
            HttpContext http, ISettingsService settings, IProtectedSecretStore secrets,
            ITimesheetImportService svc, CancellationToken ct) =>
        {
            var deny = await CheckImportAuthAsync(http, settings, secrets, ct);
            if (deny is not null) return deny;

            var users = await svc.ListUsersAsync(ct);
            return Results.Ok(new
            {
                items = users.Select(u => new
                {
                    id = u.Id,
                    email = u.Email,
                    role = u.Role,
                    isActive = u.IsActive,
                    timesheetEnabled = u.TimesheetEnabled,
                }),
            });
        }).WithName("ListTimesheetImportUsers").WithOpenApi();

        mig.MapPost("/entries", async (
            [FromBody] ImportBatchRequest req,
            HttpContext http, ISettingsService settings, IProtectedSecretStore secrets,
            ITimesheetImportService svc, IAuditLogger audit, CancellationToken ct) =>
        {
            var deny = await CheckImportAuthAsync(http, settings, secrets, ct);
            if (deny is not null) return deny;

            if (req is null || string.IsNullOrWhiteSpace(req.ImportSource))
                return Results.BadRequest(new { error = "missing_import_source" });
            if (req.Rows is null || req.Rows.Count == 0)
                return Results.BadRequest(new { error = "empty_batch" });
            if (req.Rows.Count > MaxRowsPerBatch)
                return Results.BadRequest(new { error = "batch_too_large", message = $"Max {MaxRowsPerBatch} rows per batch." });

            var rows = req.Rows.Select(r => new TimesheetImportRow(
                ImportRef: r.ImportRef ?? string.Empty,
                UserId: r.UserId,
                TaskId: r.TaskId,
                ZammadTicketNumber: r.ZammadTicketNumber,
                EntryDate: r.EntryDate,
                StartMinutes: r.StartMinutes,
                EndMinutes: r.EndMinutes,
                Invoiced: r.Invoiced,
                Description: r.Description ?? string.Empty,
                CreatedByUserId: r.CreatedByUserId,
                UpdatedByUserId: r.UpdatedByUserId,
                CreatedUtc: r.CreatedUtc,
                UpdatedUtc: r.UpdatedUtc)).ToList();

            TimesheetImportBatchResult result;
            try
            {
                result = await svc.ImportBatchAsync(req.ImportSource, rows, ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_request", message = ex.Message });
            }

            // Audit each batch as a privileged action. No session here, so
            // stamp a synthetic actor; the client IP still lands in the row.
            await audit.LogAsync(new AuditEvent(
                EventType: TimesheetAudit.ImportBatch,
                Actor: "timesheet-import",
                ActorRole: "System",
                Target: req.ImportSource.Trim(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    received = result.Received,
                    imported = result.Imported,
                    withoutTicketMatch = result.WithoutTicketMatch,
                    skipped = result.Received - result.Imported,
                }), ct);

            return Results.Ok(new
            {
                received = result.Received,
                imported = result.Imported,
                withoutTicketMatch = result.WithoutTicketMatch,
                skipped = result.Skipped.Select(s => new { importRef = s.ImportRef, reason = s.Reason }),
            });
        }).WithName("ImportTimesheetEntries").WithOpenApi();

        return app;
    }

    /// Returns a deny-result when the migration surface must not answer, or
    /// null when the caller is authorised. 404 (not 403) when the surface is
    /// disabled or unconfigured, so its existence isn't leaked.
    private static async Task<IResult?> CheckImportAuthAsync(
        HttpContext http, ISettingsService settings, IProtectedSecretStore secrets, CancellationToken ct)
    {
        // Check the master switch first so the disabled path never touches
        // the secret store. Both "off" and "no token" collapse to 404 so the
        // surface's existence is never leaked.
        var enabled = await settings.GetAsync<bool>(SettingKeys.Timesheet.ImportEnabled, ct);
        if (!enabled) return Results.NotFound();

        var secret = await secrets.GetAsync(ProtectedSecretKeys.TimesheetImportToken, ct);
        if (string.IsNullOrEmpty(secret))
            return Results.NotFound();

        var provided = http.Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(provided) || !FixedTimeEquals(provided, secret))
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

        return null;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public sealed record SetEnabledRequest(bool Enabled);
    public sealed record SetSecretRequest(string? Value);

    public sealed record ImportBatchRequest(string ImportSource, List<ImportRowDto> Rows);

    public sealed record ImportRowDto(
        string? ImportRef,
        Guid UserId,
        Guid TaskId,
        string? ZammadTicketNumber,
        DateOnly EntryDate,
        int StartMinutes,
        int EndMinutes,
        bool Invoiced,
        string? Description,
        Guid? CreatedByUserId,
        Guid? UpdatedByUserId,
        DateTimeOffset? CreatedUtc,
        DateTimeOffset? UpdatedUtc);
}
