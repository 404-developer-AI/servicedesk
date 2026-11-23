using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
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
    private static void MapAdsolutDebugRoutes(RouteGroupBuilder admin)
    {
        admin.MapGet("/debug/lookup", DebugLookup).WithName("AdsolutDebugLookup");
        admin.MapGet("/debug/erp-sales-receipts", DebugErpSalesReceipts).WithName("AdsolutDebugErpSalesReceipts");

        admin.MapGet("/debug/put-preview", DebugPutPreview).WithName("AdsolutDebugPutPreview");
        admin.MapPost("/debug/put", DebugPutCustomer).WithName("AdsolutDebugPutCustomer");
        admin.MapGet("/debug/access-token", DebugAccessToken).WithName("AdsolutDebugAccessToken")
            .RequireRateLimiting("auth");
    }

    // ---- /debug/lookup --------------------------------------------------
    //
    // Diagnostic-only: lets an admin probe Adsolut's /customers or /suppliers
    // list-endpoint with a Code= filter from the settings UI and inspect the
    // raw JSON response. Useful for confirming what fields WK actually sends
    // for a specific relation before we wire them into the upserter. We do
    // NOT proxy arbitrary paths — kind is a fixed enum and only the Code
    // filter is forwarded — so this cannot become a generic "any-API" hole.

    private static readonly HashSet<string> AllowedDebugKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "customer",
        "supplier",
    };

    private static async Task<IResult> DebugLookup(
        string? kind,
        string? code,
        IAdsolutConnectionStore connections,
        AdsolutHttpInvoker invoker,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind) || !AllowedDebugKinds.Contains(kind))
        {
            return Results.BadRequest(new { error = "invalid_kind", message = "kind must be 'customer' or 'supplier'." });
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return Results.BadRequest(new { error = "missing_code" });
        }
        var trimmed = code.Trim();
        if (trimmed.Length > 32)
        {
            return Results.BadRequest(new { error = "invalid_code", message = "code must be 32 characters or fewer." });
        }
        // Restrict to the alphabet Adsolut uses for relation codes (and a
        // few common separators). Keeps the upstream URL trivially safe and
        // blocks "code=&Limit=999" style escape attempts.
        foreach (var ch in trimmed)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '/'))
            {
                return Results.BadRequest(new { error = "invalid_code", message = "code may contain letters, digits, '-', '_', '.', '/' only." });
            }
        }

        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        if (connection.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }

        var resource = string.Equals(kind, "supplier", StringComparison.OrdinalIgnoreCase) ? "suppliers" : "customers";
        var baseUrl = await invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/{resource}?Code={Uri.EscapeDataString(trimmed)}&Limit=10";

        try
        {
            var raw = await invoker.SendRawAsync(
                eventType: AdsolutEventTypes.DebugLookup,
                buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
                auditPayload: new { kind = resource, code = trimmed, administrationId },
                ct: ct);
            return Results.Ok(new
            {
                status = raw.Status,
                requestUrl = raw.RequestUrl,
                upstreamErrorCode = raw.UpstreamErrorCode,
                body = raw.Body,
            });
        }
        catch (AdsolutApiException ex)
        {
            // Refresh / transport failure — no upstream body to show. Surface
            // it in the same envelope so the UI does not need a second code
            // path; status 0 signals "never reached Adsolut".
            return Results.Ok(new
            {
                status = ex.HttpStatus ?? 0,
                requestUrl = url,
                upstreamErrorCode = ex.UpstreamErrorCode ?? "transport_or_refresh_failed",
                body = ex.Message,
            });
        }
    }

    // ---- /debug/erp-sales-receipts -------------------------------------
    //
    // Diagnostic-only preview of the ERP SalesReceiptInfos ("verkoopbonnen")
    // list endpoint, the read path for the upcoming Timesheet → Adsolut tab.
    // Hits a single small page through the shared invoker and shows the raw
    // JSON verbatim so an admin can confirm the real `state` codes and which
    // fields WK actually serialises (notably whether a created-by/modified-by
    // *user* exists) before any mirror schema is committed. Like /debug/lookup
    // this is NOT a generic proxy: the path is fixed, only PageSize and the
    // IncludeFinishedState flag are forwarded, both clamped/coerced here.
    //
    // Requires the WK.BE.Erp.Read scope on the active refresh token — without
    // it WK answers 403; the raw body surfaces that cleanly for the admin.

    private static async Task<IResult> DebugErpSalesReceipts(
        IAdsolutConnectionStore connections,
        AdsolutHttpInvoker invoker,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        if (connection.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }

        var baseUrl = await invoker.ResolveBaseUrlAsync(ct);
        var erpAdm = $"{baseUrl}/erp/v1/adm/{administrationId}";

        // WK answered an earlier single-variant probe with an empty-body 500
        // (no error envelope), so this fans out a fixed set of read-only GETs
        // to localise the failure in one click: is it a single query param WK
        // chokes on, or the whole ERP data path for this dossier?
        //   - list (finished)  — full call (PageSize + IncludeFinishedState)
        //   - list (open only) — drop IncludeFinishedState
        //   - list (bare)      — no query params at all
        //   - count            — simplest ERP read; if this 500s too the
        //                        problem is the ERP module/provisioning, not params
        var probes = new (string Label, string Url)[]
        {
            ("list (finished)", $"{erpAdm}/SalesReceiptInfos?PageSize=5&IncludeFinishedState=true"),
            ("list (open only)", $"{erpAdm}/SalesReceiptInfos?PageSize=5"),
            ("list (bare)", $"{erpAdm}/SalesReceiptInfos"),
            ("count", $"{erpAdm}/SalesReceiptInfos/count"),
        };

        var attempts = new List<object>(probes.Length);
        foreach (var (label, url) in probes)
        {
            try
            {
                var raw = await invoker.SendRawAsync(
                    eventType: AdsolutEventTypes.DebugErpSalesReceipts,
                    buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
                    auditPayload: new { administrationId, probe = label },
                    ct: ct);
                attempts.Add(new
                {
                    label,
                    status = raw.Status,
                    requestUrl = raw.RequestUrl,
                    upstreamErrorCode = raw.UpstreamErrorCode,
                    body = raw.Body,
                });
            }
            catch (AdsolutApiException ex)
            {
                attempts.Add(new
                {
                    label,
                    status = ex.HttpStatus ?? 0,
                    requestUrl = url,
                    upstreamErrorCode = ex.UpstreamErrorCode ?? "transport_or_refresh_failed",
                    body = ex.Message,
                });
            }
        }

        return Results.Ok(new { attempts });
    }

    // ---- /debug/put-preview + /debug/put ------------------------------
    //
    // Postman-lite for the customers PUT path. The preview endpoint does
    // a single GET on /customers/{id} and runs the response through
    // AdsolutCustomersWriteClient.BuildUpdateBody(getJson, overlay: null,
    // customerId) so the admin sees the exact body the pusher would
    // produce for a no-edit round-trip. They can then optionally edit
    // that body in-browser and POST it back via /debug/put, which forwards
    // it verbatim as the request body of a PUT to /customers/{id}.
    //
    // Why two endpoints instead of one preview-and-send: the admin must
    // be able to see + (optionally) tweak the body before any mutation
    // hits Adsolut. A single round-trip would either skip the review step
    // or force an "are you sure?" dialog, neither matching the Postman-
    // workflow intent.
    //
    // Scope guard: the URL path id is the only mutable surface; everything
    // else is admin-gated and audited as debug.put_preview / debug.customer_put.
    // The supplied PUT body is sent verbatim — the admin owns the contract
    // they are testing.

    private static async Task<IResult> DebugPutPreview(
        Guid customerId,
        IAdsolutConnectionStore connections,
        AdsolutHttpInvoker invoker,
        CancellationToken ct)
    {
        if (customerId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_customer_id" });
        }

        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        if (connection.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }

        var baseUrl = await invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{customerId}";

        AdsolutRawResponse raw;
        try
        {
            raw = await invoker.SendRawAsync(
                eventType: AdsolutEventTypes.DebugPutPreview,
                buildRequest: () => new HttpRequestMessage(HttpMethod.Get, url),
                auditPayload: new { customerId, administrationId },
                ct: ct);
        }
        catch (AdsolutApiException ex)
        {
            return Results.Ok(new
            {
                getStatus = ex.HttpStatus ?? 0,
                getRequestUrl = url,
                upstreamErrorCode = ex.UpstreamErrorCode ?? "transport_or_refresh_failed",
                getBody = ex.Message,
                putUrl = url,
                putBody = (string?)null,
                putBuildError = (string?)null,
            });
        }

        // Only attempt to derive the PUT body when the GET succeeded with
        // a parseable JSON payload. A 4xx/5xx body from Adsolut is usually
        // an error envelope and feeding it into BuildUpdateBody would just
        // throw; surface the failure cleanly so the UI can show "GET
        // failed, no PUT body to preview".
        string? putBody = null;
        string? putBuildError = null;
        if (raw.Status is >= 200 and < 300 && !string.IsNullOrWhiteSpace(raw.Body))
        {
            try
            {
                putBody = AdsolutCustomersWriteClient.BuildUpdateBody(raw.Body, overlay: null, customerId);
            }
            catch (AdsolutApiException ex)
            {
                putBuildError = ex.Message;
            }
            catch (Exception ex)
            {
                putBuildError = $"build_failed: {ex.Message}";
            }
        }

        return Results.Ok(new
        {
            getStatus = raw.Status,
            getRequestUrl = raw.RequestUrl,
            upstreamErrorCode = raw.UpstreamErrorCode,
            getBody = raw.Body,
            putUrl = url,
            putBody,
            putBuildError,
        });
    }

    public sealed record DebugPutRequest(
        [property: Required] Guid CustomerId,
        [property: Required] string Body);

    private static async Task<IResult> DebugPutCustomer(
        [FromBody] DebugPutRequest request,
        IAdsolutConnectionStore connections,
        AdsolutHttpInvoker invoker,
        CancellationToken ct)
    {
        if (request is null || request.CustomerId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_customer_id" });
        }
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Results.BadRequest(new { error = "missing_body" });
        }

        // Defensive parse so a malformed paste doesn't slip through and
        // hit Adsolut as application/json with garbage; their error
        // surface for that case is hostile to debug.
        try
        {
            using var _ = JsonDocument.Parse(request.Body);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", message = ex.Message });
        }

        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        if (connection.AdministrationId is not Guid administrationId)
        {
            return Results.BadRequest(new { error = "no_administration", message = "Activate a dossier first." });
        }

        var baseUrl = await invoker.ResolveBaseUrlAsync(ct);
        var url = $"{baseUrl}/acc/v1/adm/{administrationId}/customers/{request.CustomerId}";

        try
        {
            var raw = await invoker.SendRawAsync(
                eventType: AdsolutEventTypes.DebugCustomerPut,
                buildRequest: () => new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new StringContent(request.Body, Encoding.UTF8, "application/json"),
                },
                auditPayload: new { customerId = request.CustomerId, administrationId, bodyBytes = request.Body.Length },
                ct: ct);
            return Results.Ok(new
            {
                status = raw.Status,
                requestUrl = raw.RequestUrl,
                upstreamErrorCode = raw.UpstreamErrorCode,
                body = raw.Body,
            });
        }
        catch (AdsolutApiException ex)
        {
            return Results.Ok(new
            {
                status = ex.HttpStatus ?? 0,
                requestUrl = url,
                upstreamErrorCode = ex.UpstreamErrorCode ?? "transport_or_refresh_failed",
                body = ex.Message,
            });
        }
    }

    // ---- /debug/access-token -------------------------------------------
    //
    // Reveals the current access token so an admin can paste it into a
    // local debug tool (Postman, curl, etc.) for one-off API exploration.
    // Token validity is bounded by the WK access-token lifetime (~1 hour);
    // the admin re-fetches when it expires by clicking the same button.
    //
    // Security:
    //   - Admin-only (group middleware) + rate-limited under the "auth"
    //     policy so brute-force enumeration of the audit_log via repeated
    //     calls is bounded.
    //   - Token value never lands in the audit payload — only the fact of
    //     export plus the access-token expiry. A leaked token can still be
    //     traced back to who exported it via the ClientIp + Actor + the
    //     expiry-window match.
    //   - Token is returned in the JSON body only; not echoed to logs,
    //     headers, or any cache layer. The Servicedesk dev-tools instinct
    //     is to grep network responses for tokens, so the response uses a
    //     boring property name (`accessToken`) and Cache-Control: no-store.

    private static async Task<IResult> DebugAccessToken(
        HttpContext http,
        IAdsolutAccessTokenProvider tokens,
        IAdsolutConnectionStore connections,
        ISettingsService settings,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }

        string token;
        try
        {
            token = await tokens.GetAccessTokenAsync(ct);
        }
        catch (AdsolutRefreshException ex)
        {
            return Results.BadRequest(new
            {
                error = "refresh_failed",
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            });
        }

        var baseUrl = (await settings.GetAsync<string>(SettingKeys.Adsolut.ApiBaseUrl, ct) ?? string.Empty).TrimEnd('/');

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: AdsolutEventTypes.AccessTokenExported,
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new
            {
                accessTokenExpiresUtc = connection.AccessTokenExpiresUtc,
                administrationId = connection.AdministrationId,
            }), ct);

        // Cache-Control: no-store so an intermediate proxy cannot cache
        // the token. Should never matter behind our auth-cookie + HTTPS,
        // but defence in depth is cheap.
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Pragma"] = "no-cache";

        return Results.Ok(new
        {
            accessToken = token,
            accessTokenExpiresUtc = connection.AccessTokenExpiresUtc,
            baseUrl,
            administrationId = connection.AdministrationId,
        });
    }
}
