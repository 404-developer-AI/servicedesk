using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Reporting;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Reporting;

/// Reporting API (v0.0.96): ticket statistics for external tooling.
///
/// Two surfaces with two different auth models:
///
/// 1. Admin config — <c>/api/admin/reporting</c>, session + admin. Toggles
///    the master switch and manages the pre-shared API key (only existence
///    is ever reported, never the value). The IP allow-list and list cap are
///    plain settings edited via the generic settings surface.
///
/// 2. Public surface — key-gated (no session), read-only:
///    <c>GET /api/reporting/tickets?from=…&amp;to=…[&amp;companyId=…]</c>
///    returns for the period: tickets opened, tickets closed
///    (Resolved+Closed combined), and a snapshot of all currently-open
///    tickets — each as a count plus a capped number/subject list with
///    per-section offset paging, optionally narrowed to one company.
///    <c>GET /api/reporting/companies</c> lists all companies
///    (id/name/code/active) so a consumer can resolve the ids to filter by.
///
/// The public surface is invisible (404) unless the admin both enabled it
/// AND configured the key; the same 404 answers callers outside the
/// optional IP allow-list, so existence never leaks. A present-but-wrong
/// key from an allowed IP gets 401. Every read and every rejection is
/// audited; the "reporting" rate-limit policy bounds abuse.
public static class ReportingApiEndpoints
{
    private const string KeyHeader = "X-Reporting-Api-Key";

    public static IEndpointRouteBuilder MapReportingApiEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Admin config (session + admin) --------------------------------
        var admin = app.MapGroup("/api/admin/reporting")
            .WithTags("Reporting")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", async (
            ISettingsService settings, IProtectedSecretStore secrets, CancellationToken ct) =>
        {
            var enabled = await settings.GetAsync<bool>(SettingKeys.Reporting.Enabled, ct);
            var configured = await secrets.HasAsync(ProtectedSecretKeys.ReportingApiKey, ct);
            return Results.Ok(new { enabled, keyConfigured = configured });
        }).WithName("GetReportingApiStatus").WithOpenApi();

        admin.MapPut("/enabled", async (
            [FromBody] SetEnabledRequest req,
            HttpContext http, ISettingsService settings, IAuditLogger audit, CancellationToken ct) =>
        {
            var (actor, role) = ActorContext.Resolve(http);
            await settings.SetAsync(SettingKeys.Reporting.Enabled, req.Enabled, actor, role, ct);
            await ReportingAudit.WriteAsync(audit, http, ReportingAudit.EnabledChanged,
                SettingKeys.Reporting.Enabled, new { enabled = req.Enabled });
            return Results.Ok(new { enabled = req.Enabled });
        }).WithName("SetReportingApiEnabled").WithOpenApi();

        admin.MapPut("/key", async (
            [FromBody] SetKeyRequest req,
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            var value = (req?.Value ?? string.Empty).Trim();
            // A short key is a foot-gun on an internet-facing surface;
            // require real entropy. The Settings panel generates a long
            // random key.
            if (value.Length < 24 || value.Length > 256)
                return Results.BadRequest(new { error = "invalid_key", message = "API key must be 24–256 characters." });
            await secrets.SetAsync(ProtectedSecretKeys.ReportingApiKey, value, ct);
            await ReportingAudit.WriteAsync(audit, http, ReportingAudit.KeyUpdated,
                ProtectedSecretKeys.ReportingApiKey, new { configured = true });
            return Results.NoContent();
        }).WithName("SetReportingApiKey").WithOpenApi();

        admin.MapDelete("/key", async (
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            await secrets.DeleteAsync(ProtectedSecretKeys.ReportingApiKey, ct);
            await ReportingAudit.WriteAsync(audit, http, ReportingAudit.KeyDeleted,
                ProtectedSecretKeys.ReportingApiKey, new { configured = false });
            return Results.NoContent();
        }).WithName("DeleteReportingApiKey").WithOpenApi();

        // ---- Public surface (key-gated, read-only) -------------------------
        var pub = app.MapGroup("/api/reporting")
            .WithTags("Reporting")
            .RequireRateLimiting("reporting");

        pub.MapGet("/tickets", async (
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromQuery] string? companyId,
            [FromQuery] int? openedOffset,
            [FromQuery] int? closedOffset,
            [FromQuery] int? openOffset,
            HttpContext http, ISettingsService settings, IProtectedSecretStore secrets,
            ITicketReportService reports, IAuditLogger audit, CancellationToken ct) =>
        {
            var deny = await CheckReportingAuthAsync(http, settings, secrets, audit, "tickets", ct);
            if (deny is not null) return deny;

            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return Results.BadRequest(new { error = "missing_period", message = "Both 'from' and 'to' query parameters are required (ISO 8601, e.g. 2026-08-01 or 2026-08-01T00:00:00Z)." });
            if (!TryParseUtc(from, out var fromUtc))
                return Results.BadRequest(new { error = "invalid_from", message = "'from' is not a valid ISO 8601 date/time." });
            if (!TryParseUtc(to, out var toUtc))
                return Results.BadRequest(new { error = "invalid_to", message = "'to' is not a valid ISO 8601 date/time." });
            if (fromUtc >= toUtc)
                return Results.BadRequest(new { error = "invalid_period", message = "'from' must be before 'to'. The period is interpreted as [from, to)." });

            Guid? companyGuid = null;
            if (!string.IsNullOrWhiteSpace(companyId))
            {
                if (!Guid.TryParse(companyId, out var parsed))
                    return Results.BadRequest(new { error = "invalid_company_id", message = "'companyId' is not a valid UUID. Resolve ids via GET /api/reporting/companies." });
                companyGuid = parsed;
            }

            var maxItems = Math.Clamp(
                await settings.GetAsync<int>(SettingKeys.Reporting.MaxListItems, ct), 0, 10_000);

            var report = await reports.GetPeriodReportAsync(
                fromUtc, toUtc, maxItems,
                openedOffset ?? 0, closedOffset ?? 0, openOffset ?? 0, companyGuid, ct);

            await ReportingAudit.WriteMachineAsync(audit, http, ReportingAudit.Read, "tickets", new
            {
                from = fromUtc,
                to = toUtc,
                companyId = companyGuid,
                opened = report.Opened.Count,
                closed = report.Closed.Count,
                openNow = report.OpenNow.Count,
            }, ct);

            return Results.Ok(new
            {
                from = fromUtc,
                to = toUtc,
                companyId = companyGuid,
                generatedUtc = DateTimeOffset.UtcNow,
                maxListItems = maxItems,
                opened = ShapeSection(report.Opened),
                closed = ShapeSection(report.Closed),
                openNow = ShapeSection(report.OpenNow),
            });
        }).WithName("GetReportingTickets").WithOpenApi();

        pub.MapGet("/companies", async (
            [FromQuery] int? offset,
            HttpContext http, ISettingsService settings, IProtectedSecretStore secrets,
            ITicketReportService reports, IAuditLogger audit, CancellationToken ct) =>
        {
            var deny = await CheckReportingAuthAsync(http, settings, secrets, audit, "companies", ct);
            if (deny is not null) return deny;

            var maxItems = Math.Clamp(
                await settings.GetAsync<int>(SettingKeys.Reporting.MaxListItems, ct), 0, 10_000);

            var list = await reports.ListCompaniesAsync(maxItems, offset ?? 0, ct);

            await ReportingAudit.WriteMachineAsync(audit, http, ReportingAudit.Read, "companies", new
            {
                count = list.Count,
                offset = list.Offset,
            }, ct);

            return Results.Ok(new
            {
                generatedUtc = DateTimeOffset.UtcNow,
                maxListItems = maxItems,
                count = list.Count,
                returned = list.Items.Count,
                offset = list.Offset,
                truncated = list.Truncated,
                companies = list.Items.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    code = c.Code,
                    isActive = c.IsActive,
                }),
            });
        }).WithName("GetReportingCompanies").WithOpenApi();

        return app;
    }

    private static object ShapeSection(TicketReportSection s) => new
    {
        count = s.Count,
        returned = s.Items.Count,
        offset = s.Offset,
        truncated = s.Truncated,
        tickets = s.Items.Select(i => new { number = i.Number, subject = i.Subject }),
    };

    /// Accepts ISO 8601 date or date-time. A value without an offset is
    /// taken as UTC — never the server's local zone, and client clocks are
    /// irrelevant by design.
    private static bool TryParseUtc(string value, out DateTimeOffset utc) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out utc);

    /// Returns a deny-result when the public surface must not answer, or
    /// null when the caller is authorised. 404 (not 403) when the surface
    /// is disabled, unconfigured, or the caller's IP is outside the
    /// allow-list, so its existence isn't leaked; only an allowed caller
    /// presenting a wrong key learns the surface exists (401).
    private static async Task<IResult?> CheckReportingAuthAsync(
        HttpContext http, ISettingsService settings, IProtectedSecretStore secrets,
        IAuditLogger audit, string target, CancellationToken ct)
    {
        // Master switch first so the disabled path never touches the secret
        // store. "Off" and "no key" both collapse to 404.
        var enabled = await settings.GetAsync<bool>(SettingKeys.Reporting.Enabled, ct);
        if (!enabled) return Results.NotFound();

        var key = await secrets.GetAsync(ProtectedSecretKeys.ReportingApiKey, ct);
        if (string.IsNullOrEmpty(key))
            return Results.NotFound();

        var allowList = await settings.GetAsync<string>(SettingKeys.Reporting.IpAllowList, ct);
        if (!ReportingIpAllowList.IsAllowed(allowList, http.Connection.RemoteIpAddress))
        {
            await ReportingAudit.WriteMachineAsync(audit, http, ReportingAudit.Denied,
                target, new { reason = "ip_not_allowed" }, ct);
            return Results.NotFound();
        }

        var provided = http.Request.Headers[KeyHeader].ToString();
        if (string.IsNullOrEmpty(provided) || !FixedTimeEquals(provided, key))
        {
            await ReportingAudit.WriteMachineAsync(audit, http, ReportingAudit.Denied,
                target, new { reason = "invalid_key" }, ct);
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return null;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public sealed record SetEnabledRequest(bool Enabled);
    public sealed record SetKeyRequest(string? Value);
}
