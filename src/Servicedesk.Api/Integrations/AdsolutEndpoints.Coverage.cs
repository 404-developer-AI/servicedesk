using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

public static partial class AdsolutEndpoints
{
    private static void MapAdsolutCoverageRoutes(RouteGroupBuilder admin)
    {
        // v0.0.30 — Sync coverage. The tile + overview page surface the
        // gaps between the SD-side universe and Adsolut. Admin-only;
        // mutates only via the link-by-uuid + force-push flows below.
        admin.MapGet("/coverage", GetCoverageCounts).WithName("GetAdsolutCoverageCounts");
        admin.MapGet("/coverage/companies", GetCoverageCompanies).WithName("GetAdsolutCoverageCompanies");
        admin.MapGet("/coverage/contacts", GetCoverageContacts).WithName("GetAdsolutCoverageContacts");
        admin.MapPost("/coverage/link/company/{companyId:guid}", LinkCoverageCompany)
            .WithName("LinkAdsolutCoverageCompany");
        admin.MapPost("/coverage/link/contact/{linkId:guid}", LinkCoverageContact)
            .WithName("LinkAdsolutCoverageContact");
        admin.MapPost("/coverage/force-push/company/{companyId:guid}", ForcePushCoverageCompany)
            .WithName("ForcePushAdsolutCoverageCompany");
        admin.MapPost("/coverage/force-push/contact/{linkId:guid}", ForcePushCoverageContact)
            .WithName("ForcePushAdsolutCoverageContact");
    }

    // ---- v0.0.30 Sync coverage -----------------------------------------
    //
    // Pure read-side counts + paged list helpers powering the "Sync coverage"
    // tile and the /settings/integrations/adsolut/coverage page. Mutating
    // endpoints (link, force-push) are below the read-side. All admin-gated
    // via the group middleware.

    private static async Task<IResult> GetCoverageCounts(
        IAdsolutCoverageQuery coverage,
        CancellationToken ct)
    {
        var counts = await coverage.GetCountsAsync(ct);
        return Results.Ok(new
        {
            companiesSdOnly = counts.CompaniesSdOnly,
            companiesDrift = counts.CompaniesDrift,
            contactLinksUnsynced = counts.ContactLinksUnsynced,
            contactLinksDrift = counts.ContactLinksDrift,
            contactsPureSd = counts.ContactsPureSd,
        });
    }

    private static async Task<IResult> GetCoverageCompanies(
        string? bucket,
        string? search,
        int? page,
        int? pageSize,
        IAdsolutCoverageQuery coverage,
        CancellationToken ct)
    {
        if (!TryParseCompaniesBucket(bucket, out var parsed))
        {
            return Results.BadRequest(new { error = "invalid_bucket", message = "bucket must be 'sd-only' or 'drift'." });
        }
        var result = await coverage.ListCompaniesAsync(
            parsed, search, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                code = r.Code,
                email = r.Email,
                adsolutId = r.AdsolutId,
                adsolutNumber = r.AdsolutNumber,
                adsolutLastModified = r.AdsolutLastModified,
                updatedUtc = r.UpdatedUtc,
            }),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    private static async Task<IResult> GetCoverageContacts(
        string? bucket,
        string? search,
        int? page,
        int? pageSize,
        IAdsolutCoverageQuery coverage,
        CancellationToken ct)
    {
        if (!TryParseContactsBucket(bucket, out var parsed))
        {
            return Results.BadRequest(new
            {
                error = "invalid_bucket",
                message = "bucket must be 'links-unsynced', 'links-drift' or 'pure-sd'.",
            });
        }
        var result = await coverage.ListContactsAsync(
            parsed, search, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(r => new
            {
                linkId = r.LinkId,
                contactId = r.ContactId,
                email = r.Email,
                firstName = r.FirstName,
                lastName = r.LastName,
                companyId = r.CompanyId,
                companyName = r.CompanyName,
                companyAdsolutId = r.CompanyAdsolutId,
                adsolutContactId = r.AdsolutContactId,
                adsolutLastModified = r.AdsolutLastModified,
                contactUpdatedUtc = r.ContactUpdatedUtc,
            }),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    public sealed record LinkCompanyRequest(
        [property: Required] Guid AdsolutId,
        DateTime? LastModified);

    private static async Task<IResult> LinkCoverageCompany(
        Guid companyId,
        [FromBody] LinkCompanyRequest body,
        HttpContext http,
        IAdsolutCoverageLinks coverageLinks,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (body is null || body.AdsolutId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_adsolut_id" });
        }

        // Writes the Adsolut id + last-modified only (see IAdsolutCoverageLinks).
        var linked = await coverageLinks.LinkCompanyAsync(companyId, body.AdsolutId, body.LastModified, ct);
        if (!linked)
        {
            return Results.NotFound(new { error = "company_not_found_or_inactive" });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageLinkCompany,
            Actor: actor,
            ActorRole: role,
            Target: companyId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { companyId, adsolutId = body.AdsolutId, body.LastModified }), ct);
        return Results.Ok(new { ok = true });
    }

    public sealed record LinkContactRequest(
        [property: Required] Guid AdsolutContactId,
        DateTime? LastModified);

    private static async Task<IResult> LinkCoverageContact(
        Guid linkId,
        [FromBody] LinkContactRequest body,
        HttpContext http,
        IAdsolutCoverageLinks coverageLinks,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (body is null || body.AdsolutContactId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_adsolut_contact_id" });
        }
        var linked = await coverageLinks.LinkContactAsync(linkId, body.AdsolutContactId, body.LastModified, ct);
        if (!linked)
        {
            return Results.NotFound(new
            {
                error = "link_not_found_or_company_not_linked",
                message = "Link not found, soft-deleted, or its company is not yet linked to Adsolut.",
            });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageLinkContact,
            Actor: actor,
            ActorRole: role,
            Target: linkId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { linkId, adsolutContactId = body.AdsolutContactId, body.LastModified }), ct);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> ForcePushCoverageCompany(
        Guid companyId,
        HttpContext http,
        IAdsolutConnectionStore connections,
        IAdsolutCompanyPusher pusher,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }
        var candidate = await pusher.LoadCandidateByIdAsync(companyId, ct);
        if (candidate is null)
        {
            return Results.NotFound(new { error = "company_not_found_or_inactive" });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutPushOutcome outcome;
        try
        {
            // Force both toggles ON so the toggle-gates short-circuit; the
            // hash-no-op + no-drift gates still apply (this is a force on
            // the toggle, not on the upstream call).
            outcome = await pusher.PushOneAsync(
                administrationId,
                candidate,
                new AdsolutPushOptions(true, true),
                ct);
        }
        catch (AdsolutApiException ex)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.CoverageForcePushCompany,
                Actor: actor,
                ActorRole: role,
                Target: companyId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    companyId,
                    outcome = "ApiException",
                    httpStatus = ex.HttpStatus,
                    upstreamErrorCode = ex.UpstreamErrorCode,
                }), ct);
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageForcePushCompany,
            Actor: actor,
            ActorRole: role,
            Target: companyId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { companyId, outcome = outcome.ToString() }), ct);

        return Results.Ok(new { outcome = outcome.ToString() });
    }

    private static async Task<IResult> ForcePushCoverageContact(
        Guid linkId,
        HttpContext http,
        IAdsolutConnectionStore connections,
        IAdsolutContactPusher pusher,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }
        var candidate = await pusher.LoadCandidateByLinkIdAsync(linkId, ct);
        if (candidate is null)
        {
            return Results.NotFound(new
            {
                error = "link_not_found",
                message = "Link not found, soft-deleted, or its company is not Adsolut-linked.",
            });
        }

        var (actor, role) = ActorContext.Resolve(http);
        AdsolutContactPushOutcome outcome;
        try
        {
            outcome = await pusher.PushOneAsync(
                administrationId,
                candidate,
                new AdsolutContactsPushOptions(true, true),
                ct);
        }
        catch (AdsolutApiException ex)
        {
            await audit.LogAsync(new AuditEvent(
                EventType: AdsolutEventTypes.CoverageForcePushContact,
                Actor: actor,
                ActorRole: role,
                Target: linkId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    linkId,
                    outcome = "ApiException",
                    httpStatus = ex.HttpStatus,
                    upstreamErrorCode = ex.UpstreamErrorCode,
                }), ct);
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.CoverageForcePushContact,
            Actor: actor,
            ActorRole: role,
            Target: linkId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { linkId, outcome = outcome.ToString() }), ct);

        return Results.Ok(new { outcome = outcome.ToString() });
    }

    private static bool TryParseCompaniesBucket(string? raw, out AdsolutCoverageCompaniesBucket bucket)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "sd-only":
                bucket = AdsolutCoverageCompaniesBucket.SdOnly;
                return true;
            case "drift":
                bucket = AdsolutCoverageCompaniesBucket.Drift;
                return true;
            default:
                bucket = default;
                return false;
        }
    }

    private static bool TryParseContactsBucket(string? raw, out AdsolutCoverageContactsBucket bucket)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "links-unsynced":
                bucket = AdsolutCoverageContactsBucket.LinksUnsynced;
                return true;
            case "links-drift":
                bucket = AdsolutCoverageContactsBucket.LinksDrift;
                return true;
            case "pure-sd":
                bucket = AdsolutCoverageContactsBucket.PureSd;
                return true;
            default:
                bucket = default;
                return false;
        }
    }
}
