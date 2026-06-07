using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Auth.Admin;
using Servicedesk.Infrastructure.Auth.Microsoft;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Users;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users")
            .WithTags("AdminUsers")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", List).WithName("AdminUsersList").WithOpenApi();

        group.MapGet("/m365/search", SearchM365)
            .WithName("AdminUsersSearchM365")
            .WithOpenApi();

        group.MapPost("/m365", AddFromM365)
            .WithName("AdminUsersAddFromM365")
            .WithOpenApi();

        group.MapPost("/local", AddLocal)
            .WithName("AdminUsersAddLocal")
            .WithOpenApi()
            .RequireRateLimiting("auth");

        group.MapPost("/{id:guid}/upgrade-to-m365", UpgradeToM365)
            .WithName("AdminUsersUpgradeToM365")
            .WithOpenApi();

        group.MapPut("/{id:guid}/role", UpdateRole)
            .WithName("AdminUsersUpdateRole")
            .WithOpenApi();

        group.MapPost("/{id:guid}/activate", Activate)
            .WithName("AdminUsersActivate")
            .WithOpenApi();

        group.MapPost("/{id:guid}/deactivate", Deactivate)
            .WithName("AdminUsersDeactivate")
            .WithOpenApi();

        group.MapDelete("/{id:guid}", Delete)
            .WithName("AdminUsersDelete")
            .WithOpenApi();

        group.MapPut("/{id:guid}/timesheet-flags", UpdateTimesheetFlags)
            .WithName("AdminUsersUpdateTimesheetFlags")
            .WithOpenApi();

        group.MapPut("/{id:guid}/iso-flags", UpdateIsoFlags)
            .WithName("AdminUsersUpdateIsoFlags")
            .WithOpenApi();

        group.MapPut("/{id:guid}/kb-flag", UpdateKbFlag)
            .WithName("AdminUsersUpdateKbFlag")
            .WithOpenApi();

        group.MapPut("/{id:guid}/feature-flags", UpdateFeatureFlags)
            .WithName("AdminUsersUpdateFeatureFlags")
            .WithOpenApi();

        group.MapPut("/{id:guid}/dashboard-tiles", UpdateDashboardTiles)
            .WithName("AdminUsersUpdateDashboardTiles")
            .WithOpenApi();

        group.MapGet("/{id:guid}/timesheet-preferences", GetTimesheetPreferences)
            .WithName("AdminUsersGetTimesheetPreferences")
            .WithOpenApi();

        group.MapPut("/{id:guid}/timesheet-preferences", UpdateTimesheetPreferences)
            .WithName("AdminUsersUpdateTimesheetPreferences")
            .WithOpenApi();

        return app;
    }

    // ---- list + search -----------------------------------------------------

    private static async Task<IResult> List(IUserAdminService admin, CancellationToken ct)
    {
        var rows = await admin.ListAllAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> SearchM365(
        HttpContext httpContext,
        string? q,
        int? limit,
        IGraphDirectoryClient directory,
        ISettingsService settings,
        CancellationToken ct)
    {
        // The picker is only useful once M365 login is enabled — there is
        // no way to actually finalise an "add from M365" flow otherwise.
        // 409 (not 404) so the client can render a specific message
        // instead of treating the response as a missing feature.
        var enabled = await settings.GetAsync<bool>(SettingKeys.Auth.MicrosoftEnabled, ct);
        if (!enabled)
        {
            return Results.Conflict(new { error = "M365 login is disabled. Turn it on under Settings → Mail → Microsoft Graph first." });
        }

        IReadOnlyList<GraphUserStatus> results;
        try
        {
            results = await directory.SearchUsersAsync(q, limit ?? 20, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }

        // Project into a caller-safe shape. Only fields the UI actually
        // renders — we don't leak accountEnabled / UPN beyond what the
        // picker needs.
        var payload = results.Select(u => new
        {
            oid = u.Oid,
            displayName = u.DisplayName,
            userPrincipalName = u.UserPrincipalName,
            mail = u.Mail,
            accountEnabled = u.AccountEnabled,
        });
        return Results.Ok(payload);
    }

    // ---- add-local ---------------------------------------------------------

    public sealed record AddLocalRequest(
        [property: Required] string Email,
        [property: Required] string Password,
        [property: Required] string Role);

    private static async Task<IResult> AddLocal(
        [FromBody] AddLocalRequest request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var result = await admin.AddLocalUserAsync(
            request.Email ?? string.Empty,
            request.Password ?? string.Empty,
            request.Role ?? string.Empty,
            adminId.Value,
            ct);

        return result switch
        {
            AddLocalUserResult.Created created =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserCreatedLocal,
                    target: created.Row.Id.ToString(),
                    payload: new { email = created.Row.Email, role = created.Row.Role },
                    body: created.Row,
                    statusCode: StatusCodes.Status201Created,
                    ct: ct),
            AddLocalUserResult.InvalidEmail =>
                Results.BadRequest(new { error = "A valid email is required." }),
            AddLocalUserResult.InvalidRole =>
                Results.BadRequest(new { error = "Role must be Agent or Admin." }),
            AddLocalUserResult.WeakPassword weak =>
                Results.BadRequest(new { error = $"Password must be at least {weak.MinimumLength} characters." }),
            AddLocalUserResult.EmailAlreadyUsed =>
                Results.Conflict(new { error = "That email is already in use." }),
            _ => Results.Problem("Unhandled add-local result."),
        };
    }

    // ---- add-from-M365 -----------------------------------------------------

    public sealed record AddFromM365Request(
        [property: Required] string Oid,
        [property: Required] string Role);

    private static async Task<IResult> AddFromM365(
        [FromBody] AddFromM365Request request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Oid))
        {
            return Results.BadRequest(new { error = "OID is required." });
        }

        var result = await admin.AddMicrosoftUserAsync(request.Oid.Trim(), request.Role, adminId.Value, ct);
        return result switch
        {
            AddMicrosoftUserResult.Created created =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserCreatedMicrosoft,
                    target: created.Row.Id.ToString(),
                    payload: new { email = created.Row.Email, role = created.Row.Role, oid = created.Row.ExternalSubject },
                    body: created.Row,
                    statusCode: StatusCodes.Status201Created,
                    ct: ct),
            AddMicrosoftUserResult.InvalidRole =>
                Results.BadRequest(new { error = "Role must be Agent or Admin." }),
            AddMicrosoftUserResult.OidNotFound =>
                Results.NotFound(new { error = "That Azure AD object-id does not exist in this tenant." }),
            AddMicrosoftUserResult.OidAlreadyLinked =>
                Results.Conflict(new { error = "That user is already linked to an account." }),
            AddMicrosoftUserResult.EmailAlreadyUsed =>
                Results.Conflict(new { error = "A local account already uses that email. Use 'Upgrade to M365' on the existing row instead." }),
            AddMicrosoftUserResult.AzureAccountDisabled =>
                Results.Conflict(new { error = "That Azure AD account is disabled. Enable it in Azure and retry." }),
            AddMicrosoftUserResult.MissingGraphConfig =>
                Results.Conflict(new { error = "Microsoft Graph is not fully configured. Set tenant / client / secret first." }),
            _ => Results.Problem("Unhandled add-from-M365 result."),
        };
    }

    // ---- upgrade-local-to-M365 --------------------------------------------

    public sealed record UpgradeRequest([property: Required] string Oid);

    private static async Task<IResult> UpgradeToM365(
        Guid id,
        [FromBody] UpgradeRequest request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Oid))
        {
            return Results.BadRequest(new { error = "OID is required." });
        }

        var result = await admin.UpgradeToMicrosoftAsync(id, request.Oid.Trim(), adminId.Value, ct);
        return result switch
        {
            UpgradeToMicrosoftResult.Upgraded upgraded =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserUpgradedMicrosoft,
                    target: upgraded.Row.Id.ToString(),
                    payload: new { email = upgraded.Row.Email, oid = upgraded.Row.ExternalSubject },
                    body: upgraded.Row,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            UpgradeToMicrosoftResult.UserNotFound => Results.NotFound(),
            UpgradeToMicrosoftResult.NotLocalUser =>
                Results.Conflict(new { error = "This user is already linked to Microsoft." }),
            UpgradeToMicrosoftResult.OidNotFound =>
                Results.NotFound(new { error = "That Azure AD object-id does not exist in this tenant." }),
            UpgradeToMicrosoftResult.OidAlreadyLinked =>
                Results.Conflict(new { error = "That Azure object is already linked to another account." }),
            UpgradeToMicrosoftResult.AzureAccountDisabled =>
                Results.Conflict(new { error = "That Azure AD account is disabled." }),
            UpgradeToMicrosoftResult.SelfUpgradeForbidden =>
                Results.Conflict(new { error = "You cannot upgrade your own account. Ask another admin to do it." }),
            UpgradeToMicrosoftResult.MissingGraphConfig =>
                Results.Conflict(new { error = "Microsoft Graph is not fully configured." }),
            _ => Results.Problem("Unhandled upgrade-to-M365 result."),
        };
    }

    // ---- role / activate / deactivate / delete ----------------------------

    public sealed record UpdateRoleRequest([property: Required] string Role);

    private static async Task<IResult> UpdateRole(
        Guid id,
        [FromBody] UpdateRoleRequest request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var result = await admin.UpdateRoleAsync(id, request.Role, adminId.Value, ct);
        return result switch
        {
            UpdateRoleResult.Updated updated =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserRoleChanged,
                    target: updated.Row.Id.ToString(),
                    payload: new { email = updated.Row.Email, role = updated.Row.Role },
                    body: updated.Row,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            UpdateRoleResult.UserNotFound => Results.NotFound(),
            UpdateRoleResult.InvalidRole => Results.BadRequest(new { error = "Role must be Agent or Admin." }),
            UpdateRoleResult.SelfChangeForbidden => Results.Conflict(new { error = "You cannot change your own role." }),
            UpdateRoleResult.LastAdminForbidden => Results.Conflict(new { error = "At least one active Admin must remain." }),
            _ => Results.Problem("Unhandled role-change result."),
        };
    }

    private static Task<IResult> Activate(Guid id, HttpContext ctx, IUserAdminService a, IAuditLogger al, CancellationToken ct) =>
        ToggleActiveAsync(id, active: true, ctx, a, al, ct);

    private static Task<IResult> Deactivate(Guid id, HttpContext ctx, IUserAdminService a, IAuditLogger al, CancellationToken ct) =>
        ToggleActiveAsync(id, active: false, ctx, a, al, ct);

    private static async Task<IResult> ToggleActiveAsync(
        Guid id,
        bool active,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var result = await admin.SetActiveAsync(id, active, adminId.Value, ct);
        return result switch
        {
            SetActiveResult.Updated updated =>
                await LogAndReturnAsync(httpContext, audit,
                    active ? AuthEventTypes.UserActivated : AuthEventTypes.UserDeactivated,
                    target: updated.Row.Id.ToString(),
                    payload: new { email = updated.Row.Email },
                    body: updated.Row,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            SetActiveResult.UserNotFound => Results.NotFound(),
            SetActiveResult.SelfChangeForbidden => Results.Conflict(new { error = "You cannot deactivate your own account." }),
            SetActiveResult.LastAdminForbidden => Results.Conflict(new { error = "At least one active Admin must remain." }),
            _ => Results.Problem("Unhandled set-active result."),
        };
    }

    private static async Task<IResult> Delete(
        Guid id,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var result = await admin.DeleteAsync(id, adminId.Value, ct);
        return result switch
        {
            DeleteResult.Deleted =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserDeleted,
                    target: id.ToString(),
                    payload: null,
                    body: null,
                    statusCode: StatusCodes.Status204NoContent,
                    ct: ct),
            DeleteResult.UserNotFound => Results.NotFound(),
            DeleteResult.SelfDeleteForbidden => Results.Conflict(new { error = "You cannot delete your own account." }),
            DeleteResult.LastAdminForbidden => Results.Conflict(new { error = "At least one active Admin must remain." }),
            DeleteResult.BlockedReferences blocked =>
                Results.Conflict(new { error = "This user still has activity: " + string.Join("; ", blocked.Reasons) + ". Deactivate them instead." }),
            _ => Results.Problem("Unhandled delete result."),
        };
    }

    // ---- timesheet feature flags (v0.0.35) ---------------------------------

    public sealed record UpdateTimesheetFlagsRequest(bool Enabled, bool Manager);

    public sealed record UpdateIsoFlagsRequest(bool Mgm, bool Dpo);

    public sealed record UpdateKbFlagRequest(bool Enabled);

    public sealed record UpdateFeatureFlagsRequest(
        bool? TimesheetEnabled,
        bool? TimesheetManager,
        bool? IsIsoMgm,
        bool? IsIsoDpo,
        bool? KbEnabled,
        bool? SearchEnabled,
        bool? ActivityFeedEnabled,
        bool? AssetsEnabled,
        bool? AdsolutTimesheetEnabled,
        bool? TimesheetBackofficeEnabled,
        bool? AdsolutOrdersEnabled,
        bool? StatisticsRead,
        bool? StatisticsWrite);

    private static async Task<IResult> UpdateFeatureFlags(
        Guid id,
        [FromBody] UpdateFeatureFlagsRequest request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var update = new FeatureFlagsUpdate(
            request.TimesheetEnabled,
            request.TimesheetManager,
            request.IsIsoMgm,
            request.IsIsoDpo,
            request.KbEnabled,
            request.SearchEnabled,
            request.ActivityFeedEnabled,
            request.AssetsEnabled,
            request.AdsolutTimesheetEnabled,
            request.TimesheetBackofficeEnabled,
            request.AdsolutOrdersEnabled,
            request.StatisticsRead,
            request.StatisticsWrite);

        var result = await admin.UpdateFeatureFlagsAsync(id, update, adminId.Value, ct);
        return result switch
        {
            UpdateFeatureFlagsResult.Updated updated =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserFeatureFlagsChanged,
                    target: updated.Row.Id.ToString(),
                    payload: new
                    {
                        email = updated.Row.Email,
                        timesheet_enabled = updated.Row.TimesheetEnabled,
                        timesheet_manager = updated.Row.TimesheetManager,
                        is_iso_mgm = updated.Row.IsIsoMgm,
                        is_iso_dpo = updated.Row.IsIsoDpo,
                        kb_enabled = updated.Row.KbEnabled,
                        search_enabled = updated.Row.SearchEnabled,
                        activity_feed_enabled = updated.Row.ActivityFeedEnabled,
                        assets_enabled = updated.Row.AssetsEnabled,
                        adsolut_timesheet_enabled = updated.Row.AdsolutTimesheetEnabled,
                        timesheet_backoffice_enabled = updated.Row.TimesheetBackofficeEnabled,
                        adsolut_orders_enabled = updated.Row.AdsolutOrdersEnabled,
                        statistics_read = updated.Row.StatisticsRead,
                        statistics_write = updated.Row.StatisticsWrite,
                    },
                    body: updated.Row,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            UpdateFeatureFlagsResult.UserNotFound => Results.NotFound(),
            UpdateFeatureFlagsResult.CustomerNotAllowed =>
                Results.Conflict(new { error = "Feature flags are only available for Agent and Admin accounts." }),
            UpdateFeatureFlagsResult.NoChange => Results.Ok(),
            _ => Results.Problem("Unhandled feature-flags result."),
        };
    }

    public sealed record UpdateDashboardTilesRequest(IReadOnlyList<string> TileIds);

    private static async Task<IResult> UpdateDashboardTiles(
        Guid id,
        [FromBody] UpdateDashboardTilesRequest request,
        HttpContext httpContext,
        Servicedesk.Infrastructure.Dashboard.IDashboardTilesService dashboardTiles,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var requested = request.TileIds ?? Array.Empty<string>();
        // Admin endpoint only toggles enable/disable; size + position
        // belong to the user's own edit-mode flow. Newly-enabled tiles
        // default to 'medium' and get appended at the end of the user's
        // existing layout — established positions/sizes are preserved.
        var tileSizes = requested
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                id => id,
                _ => Servicedesk.Infrastructure.Dashboard.DashboardTileSizes.Medium,
                StringComparer.Ordinal);

        var result = await dashboardTiles.SetEnabledForUserAsync(id, tileSizes, adminId.Value, ct);
        return result switch
        {
            Servicedesk.Infrastructure.Dashboard.SetDashboardTilesResult.Updated upd =>
                await ReturnRowAfterTilesChangeAsync(
                    httpContext, audit, admin, id, upd.Tiles, ct),
            Servicedesk.Infrastructure.Dashboard.SetDashboardTilesResult.UserNotFound =>
                Results.NotFound(),
            Servicedesk.Infrastructure.Dashboard.SetDashboardTilesResult.CustomerNotAllowed =>
                Results.Conflict(new { error = "Dashboard tiles are only available for Agent and Admin accounts." }),
            Servicedesk.Infrastructure.Dashboard.SetDashboardTilesResult.UnknownTileIds unknown =>
                Results.BadRequest(new
                {
                    error = "Unknown tile id(s).",
                    unknown_tile_ids = unknown.Ids,
                }),
            Servicedesk.Infrastructure.Dashboard.SetDashboardTilesResult.InvalidSize bad =>
                Results.BadRequest(new { error = "Invalid tile size.", size = bad.Size }),
            _ => Results.Problem("Unhandled dashboard-tiles result."),
        };
    }

    private static async Task<IResult> ReturnRowAfterTilesChangeAsync(
        HttpContext httpContext,
        IAuditLogger audit,
        IUserAdminService admin,
        Guid userId,
        IReadOnlyList<Servicedesk.Infrastructure.Dashboard.DashboardTilePref> tiles,
        CancellationToken ct)
    {
        var row = await admin.GetByIdAsync(userId, ct);
        if (row is null) return Results.NotFound();
        return await LogAndReturnAsync(httpContext, audit,
            AuthEventTypes.UserDashboardTilesChanged,
            target: row.Id.ToString(),
            payload: new
            {
                email = row.Email,
                tiles = tiles.Select(t => new { tile_id = t.TileId, size = t.Size }).ToList(),
            },
            body: row,
            statusCode: StatusCodes.Status200OK,
            ct: ct);
    }

    private static async Task<IResult> UpdateKbFlag(
        Guid id,
        [FromBody] UpdateKbFlagRequest request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var result = await admin.UpdateKbFlagAsync(
            id, request.Enabled, adminId.Value, ct);
        return result switch
        {
            UpdateKbFlagResult.Updated updated =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserKbFlagChanged,
                    target: updated.Row.Id.ToString(),
                    payload: new
                    {
                        email = updated.Row.Email,
                        kb_enabled = updated.Row.KbEnabled,
                    },
                    body: updated.Row,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            UpdateKbFlagResult.UserNotFound => Results.NotFound(),
            UpdateKbFlagResult.CustomerNotAllowed =>
                Results.Conflict(new { error = "Knowledge Base access is only available for Agent and Admin accounts." }),
            _ => Results.Problem("Unhandled kb-flag result."),
        };
    }

    private static async Task<IResult> UpdateIsoFlags(
        Guid id,
        [FromBody] UpdateIsoFlagsRequest request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var result = await admin.UpdateIsoFlagsAsync(
            id, request.Mgm, request.Dpo, adminId.Value, ct);
        return result switch
        {
            UpdateIsoFlagsResult.Updated updated =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserIsoFlagsChanged,
                    target: updated.Row.Id.ToString(),
                    payload: new
                    {
                        email = updated.Row.Email,
                        is_iso_mgm = updated.Row.IsIsoMgm,
                        is_iso_dpo = updated.Row.IsIsoDpo,
                    },
                    body: updated.Row,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            UpdateIsoFlagsResult.UserNotFound => Results.NotFound(),
            UpdateIsoFlagsResult.CustomerNotAllowed =>
                Results.Conflict(new { error = "ISO 27001 flags are only available for Agent and Admin accounts." }),
            _ => Results.Problem("Unhandled iso-flags result."),
        };
    }

    private static async Task<IResult> UpdateTimesheetFlags(
        Guid id,
        [FromBody] UpdateTimesheetFlagsRequest request,
        HttpContext httpContext,
        IUserAdminService admin,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var adminId = RequireUserId(httpContext);
        if (adminId is null) return Results.Unauthorized();

        var result = await admin.UpdateTimesheetFlagsAsync(
            id, request.Enabled, request.Manager, adminId.Value, ct);
        return result switch
        {
            UpdateTimesheetFlagsResult.Updated updated =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserTimesheetFlagsChanged,
                    target: updated.Row.Id.ToString(),
                    payload: new
                    {
                        email = updated.Row.Email,
                        timesheet_enabled = updated.Row.TimesheetEnabled,
                        timesheet_manager = updated.Row.TimesheetManager,
                    },
                    body: updated.Row,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            UpdateTimesheetFlagsResult.UserNotFound => Results.NotFound(),
            UpdateTimesheetFlagsResult.CustomerNotAllowed =>
                Results.Conflict(new { error = "Timesheet flags are only available for Agent and Admin accounts." }),
            _ => Results.Problem("Unhandled timesheet-flags result."),
        };
    }

    // ---- v0.0.35-E timesheet preferences (per-user override CRUD) ---------

    public sealed record TimesheetPreferencesRequest(
        int? DayStartMinutes,
        int? TargetMinutesPerDay,
        int? TargetMinutesPerWeek,
        IReadOnlyList<int>? WorkDays,
        int? MaxAbsenceMinutesPerDay,
        int? OfficeStartMinutes,
        int? OfficeEndMinutes);

    private static async Task<IResult> GetTimesheetPreferences(
        Guid id,
        Servicedesk.Infrastructure.Timesheet.ITimesheetPreferencesService prefs,
        CancellationToken ct)
    {
        var effective = await prefs.GetEffectiveAsync(id, ct);
        var overrideValues = await prefs.GetOverrideAsync(id, ct);
        // Return both: the override-fields show "what the admin has set"
        // (with nulls where they're falling back), the effective bundle
        // shows "what the user actually sees" once the merge applies.
        return Results.Ok(new
        {
            effective,
            @override = overrideValues ?? new Servicedesk.Infrastructure.Timesheet.TimesheetOverride(null, null, null, null, null, null, null),
        });
    }

    private static async Task<IResult> UpdateTimesheetPreferences(
        Guid id,
        [FromBody] TimesheetPreferencesRequest request,
        HttpContext httpContext,
        Servicedesk.Infrastructure.Timesheet.ITimesheetPreferencesService prefs,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var input = new Servicedesk.Infrastructure.Timesheet.TimesheetOverrideInput(
            DayStartMinutes: request.DayStartMinutes,
            TargetMinutesPerDay: request.TargetMinutesPerDay,
            TargetMinutesPerWeek: request.TargetMinutesPerWeek,
            WorkDays: request.WorkDays,
            MaxAbsenceMinutesPerDay: request.MaxAbsenceMinutesPerDay,
            OfficeStartMinutes: request.OfficeStartMinutes,
            OfficeEndMinutes: request.OfficeEndMinutes);

        var result = await prefs.UpdateOverrideAsync(id, input, ct);
        return result switch
        {
            Servicedesk.Infrastructure.Timesheet.UpdateOverrideResult.Updated upd =>
                await LogAndReturnAsync(httpContext, audit,
                    AuthEventTypes.UserTimesheetPreferencesChanged,
                    target: id.ToString(),
                    payload: new
                    {
                        user_id = id,
                        day_start_minutes = upd.Override.DayStartMinutes,
                        target_minutes_per_day = upd.Override.TargetMinutesPerDay,
                        target_minutes_per_week = upd.Override.TargetMinutesPerWeek,
                        work_days = upd.Override.WorkDays,
                        max_absence_minutes_per_day = upd.Override.MaxAbsenceMinutesPerDay,
                        office_start_minutes = upd.Override.OfficeStartMinutes,
                        office_end_minutes = upd.Override.OfficeEndMinutes,
                    },
                    body: upd.Override,
                    statusCode: StatusCodes.Status200OK,
                    ct: ct),
            Servicedesk.Infrastructure.Timesheet.UpdateOverrideResult.UserNotFound => Results.NotFound(),
            Servicedesk.Infrastructure.Timesheet.UpdateOverrideResult.ValidationFailed v =>
                Results.UnprocessableEntity(new { errors = v.Errors }),
            _ => Results.Problem("Unhandled timesheet-preferences result."),
        };
    }

    // ---- helpers ----------------------------------------------------------

    private static Guid? RequireUserId(HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static async Task<IResult> LogAndReturnAsync(
        HttpContext httpContext,
        IAuditLogger audit,
        string eventType,
        string? target,
        object? payload,
        object? body,
        int statusCode,
        CancellationToken ct)
    {
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: httpContext.User.Identity?.Name ?? "unknown",
            ActorRole: httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? "Admin",
            Target: target,
            ClientIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent: httpContext.Request.Headers.UserAgent.ToString(),
            Payload: payload), ct);

        return statusCode switch
        {
            StatusCodes.Status201Created => Results.Created($"/api/admin/users/{target}", body),
            StatusCodes.Status204NoContent => Results.NoContent(),
            _ => body is null ? Results.Ok() : Results.Ok(body),
        };
    }
}
