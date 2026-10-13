using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Integrations.Telavox;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// Telavox call-event webhook PROBE (v0.1.36), step 1 of moving the call
/// popup from 10 s CAPI polling to push. Telavox webhooks (per user, events
/// ringing / answer / hangup) post to
/// <c>/api/public/telavox/call-events?event={system.event}&amp;…</c> with
/// <c>Authorization: Bearer &lt;token&gt;</c>. The probe only records what
/// arrives — query string, form fields or a JSON body, phone numbers masked —
/// in an in-memory buffer the admin reads on the Telavox page. Nothing acts
/// on an event and nothing is stored in the database.
///
/// Same security shape as the contact lookup: 404 unless switched on with a
/// token; a wrong token is a 401 plus an audit row; own rate limit; exempt
/// from CSRF only because a browser cannot forge the Authorization header.
public static class TelavoxEventProbeEndpoints
{
    public const string PublicPath = "/api/public/telavox/call-events";
    private const string BearerPrefix = "Bearer ";
    private const int MaxBodyBytes = 8 * 1024;

    public static IEndpointRouteBuilder MapTelavoxEventProbeEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Admin (session + admin) ---------------------------------------
        var admin = app.MapGroup("/api/admin/integrations/telavox/event-probe")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/", async (
            ISettingsService settings, IProtectedSecretStore secrets, TelavoxEventProbeBuffer buffer, CancellationToken ct) =>
        {
            var (events, received) = buffer.Snapshot();
            return Results.Ok(new
            {
                enabled = await settings.GetAsync<bool>(SettingKeys.Telavox.EventProbeEnabled, ct),
                tokenConfigured = await secrets.HasAsync(ProtectedSecretKeys.TelavoxEventProbeToken, ct),
                path = PublicPath,
                received,
                events = events.Select(e => new
                {
                    receivedUtc = e.ReceivedUtc,
                    method = e.Method,
                    contentType = e.ContentType,
                    fields = e.Fields.Select(f => new { source = f.Source, name = f.Name, value = f.Value }),
                    agentEmail = e.AgentEmail,
                    agentMatchesUser = e.AgentMatchesUser,
                }),
            });
        }).WithName("GetTelavoxEventProbe").WithOpenApi();

        admin.MapDelete("/events", (TelavoxEventProbeBuffer buffer) =>
        {
            buffer.Clear();
            return Results.NoContent();
        }).WithName("ClearTelavoxEventProbe").WithOpenApi();

        admin.MapPost("/token", async (
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await secrets.SetAsync(ProtectedSecretKeys.TelavoxEventProbeToken, token, ct);
            await WriteAdminAuditAsync(audit, http, TelavoxEventTypes.EventProbeTokenGenerated, new { configured = true }, ct);
            return Results.Ok(new { token });
        }).WithName("GenerateTelavoxEventProbeToken").WithOpenApi();

        admin.MapDelete("/token", async (
            HttpContext http, IProtectedSecretStore secrets, IAuditLogger audit, CancellationToken ct) =>
        {
            await secrets.DeleteAsync(ProtectedSecretKeys.TelavoxEventProbeToken, ct);
            await WriteAdminAuditAsync(audit, http, TelavoxEventTypes.EventProbeTokenDeleted, new { configured = false }, ct);
            return Results.NoContent();
        }).WithName("DeleteTelavoxEventProbeToken").WithOpenApi();

        // ---- Public receiver (token-gated, log-only) ----------------------
        // GET too: Telavox lets the admin pick the method per webhook, and
        // the probe should show what either one carries.
        app.MapPost(PublicPath, Receive)
            .WithTags("TelavoxPublic")
            .RequireRateLimiting("telavox-events")
            .WithName("TelavoxCallEventProbe")
            .WithOpenApi();
        app.MapGet(PublicPath, Receive)
            .WithTags("TelavoxPublic")
            .RequireRateLimiting("telavox-events")
            .WithName("TelavoxCallEventProbeGet")
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Receive(
        HttpContext http, ISettingsService settings, IProtectedSecretStore secrets,
        IUserService users, TelavoxEventProbeBuffer buffer, IAuditLogger audit, CancellationToken ct)
    {
        if (!await settings.GetAsync<bool>(SettingKeys.Telavox.EventProbeEnabled, ct))
            return Results.NotFound();
        var token = await secrets.GetAsync(ProtectedSecretKeys.TelavoxEventProbeToken, ct);
        if (string.IsNullOrEmpty(token))
            return Results.NotFound();

        var header = http.Request.Headers.Authorization.ToString();
        var provided = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : string.Empty;
        if (provided.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(token)))
        {
            await audit.LogAsync(new AuditEvent(
                EventType: TelavoxEventTypes.EventProbeDenied,
                Actor: "telavox-events",
                ActorRole: "System",
                Target: PublicPath,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { reason = provided.Length == 0 ? "missing_token" : "invalid_token" }), ct);
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var raw = new List<(string Source, string Name, string? Value)>();
        foreach (var q in http.Request.Query)
            raw.Add(("query", q.Key, q.Value.ToString()));
        raw.AddRange(await ReadBodyAsync(http.Request, ct));
        var fields = TelavoxEventProbeBuffer.Sanitize(raw);

        // The agent's e-mail ({system.email}) is what a push popup would key
        // on — tell the admin whether it matches an active staff account.
        var email = fields.Select(f => f.Value.Trim()).FirstOrDefault(v => v.Contains('@') && v.Length <= 254);
        bool? matches = null;
        if (email is not null)
        {
            var user = await users.FindByEmailAsync(email, ct);
            matches = user is not null && user.IsActive && user.RoleName is "Agent" or "Admin";
        }

        buffer.Add(new TelavoxProbeEvent(
            DateTime.UtcNow, http.Request.Method, http.Request.ContentType, fields, email, matches));
        return Results.Ok(new { ok = true });
    }

    /// Form fields, top-level JSON properties, or the raw text — bounded.
    private static async Task<IEnumerable<(string, string, string?)>> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(request.Method) || request.ContentLength is 0) return Array.Empty<(string, string, string?)>();
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            return form.Select(f => ("form", f.Key, (string?)f.Value.ToString())).ToList();
        }

        var buffer = new byte[MaxBodyBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0) break;
            read += n;
        }
        if (read == 0) return Array.Empty<(string, string, string?)>();
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.EnumerateObject()
                    .Select(p => ("json", p.Name, (string?)(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())))
                    .ToList();
        }
        catch (JsonException)
        {
            // Not JSON — shown as raw text below.
        }
        return new[] { ("body", "raw", (string?)text) };
    }

    private static Task WriteAdminAuditAsync(
        IAuditLogger audit, HttpContext http, string eventType, object payload, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        return audit.LogAsync(new AuditEvent(
            EventType: eventType, Actor: actor, ActorRole: role,
            Target: ProtectedSecretKeys.TelavoxEventProbeToken,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload), ct);
    }
}
