using System.Security.Cryptography;
using System.Text;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Telavox;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Phones;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// Telavox "Custom Contact Lookup" (v0.1.35). A per-user Telavox webhook on
/// the ringing event calls
/// <c>GET /api/public/telavox/contact-lookup?number={system.caller}</c> with
/// <c>Authorization: Bearer &lt;token&gt;</c>; the call widget in the Telavox
/// app shows the returned `label` (name · company; `name` and `company` are
/// also returned separately). Complements the in-app popup.
///
/// Security shape (same as the Reporting API): invisible (404) unless the
/// switch is on AND a token exists; a wrong token gets 401 and an audit row;
/// the "telavox-lookup" rate limit bounds number-enumeration. The answer is
/// name + company only. The number travels in the query string, which
/// neither nginx (`$uri`) nor the request log records. No row per lookup —
/// in-memory counters for the admin panel instead.
public static class TelavoxContactLookupEndpoints
{
    public const string PublicPath = "/api/public/telavox/contact-lookup";
    private const string BearerPrefix = "Bearer ";
    private const int MaxNumberLength = 32;

    public static IEndpointRouteBuilder MapTelavoxContactLookupEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Admin config (session + admin) --------------------------------
        var admin = app.MapGroup("/api/admin/integrations/telavox/contact-lookup")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/status", async (
            ISettingsService settings, IProtectedSecretStore secrets, TelavoxContactLookupStats stats,
            CancellationToken ct) =>
        {
            var enabled = await settings.GetAsync<bool>(SettingKeys.Telavox.ContactLookupEnabled, ct);
            var configured = await secrets.HasAsync(ProtectedSecretKeys.TelavoxContactLookupToken, ct);
            var (found, notFound, denied, last) = stats.Snapshot();
            return Results.Ok(new
            {
                enabled,
                tokenConfigured = configured,
                path = PublicPath,
                found,
                notFound,
                denied,
                lastLookupUtc = last,
            });
        }).WithName("GetTelavoxContactLookupStatus").WithOpenApi();

        // The on/off switch is the regular `Telavox.ContactLookup.Enabled`
        // setting (settings API, audited as setting_changed).

        // Generated server-side (256 bits) and shown once; generating again
        // rotates it, so the old one stops working immediately.
        admin.MapPost("/token", async (
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await secrets.SetAsync(ProtectedSecretKeys.TelavoxContactLookupToken, token, ct);
            await WriteAdminAuditAsync(audit, http, TelavoxEventTypes.ContactLookupTokenGenerated,
                new { configured = true }, ct);
            return Results.Ok(new { token });
        }).WithName("GenerateTelavoxContactLookupToken").WithOpenApi();

        admin.MapDelete("/token", async (
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            await secrets.DeleteAsync(ProtectedSecretKeys.TelavoxContactLookupToken, ct);
            await WriteAdminAuditAsync(audit, http, TelavoxEventTypes.ContactLookupTokenDeleted,
                new { configured = false }, ct);
            return Results.NoContent();
        }).WithName("DeleteTelavoxContactLookupToken").WithOpenApi();

        // ---- Public lookup (token-gated, read-only) ------------------------
        app.MapGet(PublicPath, async (
            string? number,
            HttpContext http,
            ISettingsService settings,
            IProtectedSecretStore secrets,
            IContactPhoneNormalizer normalizer,
            ICompanyRepository companies,
            TelavoxContactLookupStats stats,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            // Master switch first so the disabled path never touches the
            // secret store. "Off" and "no token" both collapse to 404.
            if (!await settings.GetAsync<bool>(SettingKeys.Telavox.ContactLookupEnabled, ct))
                return Results.NotFound();
            var token = await secrets.GetAsync(ProtectedSecretKeys.TelavoxContactLookupToken, ct);
            if (string.IsNullOrEmpty(token))
                return Results.NotFound();

            var header = http.Request.Headers.Authorization.ToString();
            var provided = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? header[BearerPrefix.Length..].Trim()
                : string.Empty;
            if (provided.Length == 0 || !FixedTimeEquals(provided, token))
            {
                stats.RecordDenied();
                await audit.LogAsync(new AuditEvent(
                    EventType: TelavoxEventTypes.ContactLookupDenied,
                    Actor: "telavox-lookup",
                    ActorRole: "System",
                    Target: PublicPath,
                    ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: http.Request.Headers.UserAgent.ToString(),
                    Payload: new { reason = provided.Length == 0 ? "missing_token" : "invalid_token" }), ct);
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var e164 = await normalizer.NormalizeAsync(NormalizeQueryNumber(number), ct);
            var rows = e164.Length == 0
                ? Array.Empty<Servicedesk.Domain.Companies.ContactPhoneLookupRow>()
                : await companies.LookupContactsByPhoneE164Async(e164, 25, ct);
            var result = TelavoxContactLookup.Resolve(rows);
            if (result is null)
            {
                stats.RecordNotFound(DateTime.UtcNow);
                return Results.NotFound();
            }

            stats.RecordFound(DateTime.UtcNow);
            return Results.Ok(new { name = result.Name, company = result.Company, label = result.Label });
        })
        .WithTags("TelavoxPublic")
        .RequireRateLimiting("telavox-lookup")
        .WithName("TelavoxContactLookup")
        .WithOpenApi();

        return app;
    }

    /// `{system.caller}` arrives as "+32…" in a query string, where an
    /// unencoded '+' decodes to a space. Restore it, and cap the length so
    /// the parser never sees junk.
    internal static string NormalizeQueryNumber(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxNumberLength) return string.Empty;
        return raw.StartsWith(' ') ? "+" + raw.TrimStart() : raw.Trim();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Task WriteAdminAuditAsync(
        IAuditLogger audit, HttpContext http, string eventType, object payload, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return audit.LogAsync(new AuditEvent(
            EventType: eventType, Actor: actor, ActorRole: role,
            Target: ProtectedSecretKeys.TelavoxContactLookupToken,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload), ct);
    }
}
