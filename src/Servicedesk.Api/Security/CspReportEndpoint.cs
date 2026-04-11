using System.Text.Json;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Security;

public static class CspReportEndpoint
{
    public static IEndpointRouteBuilder MapCspReportEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/security/csp-report", async (HttpContext ctx, IAuditLogger audit, CancellationToken ct) =>
        {
            object? report = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                report = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
            }
            catch
            {
                report = new { malformed = true };
            }

            await audit.LogAsync(new AuditEvent(
                EventType: "csp_violation",
                Actor: "browser",
                ActorRole: "anon",
                ClientIp: ctx.Connection.RemoteIpAddress?.ToString(),
                UserAgent: ctx.Request.Headers.UserAgent.ToString(),
                Payload: report), ct);

            return Results.NoContent();
        })
        .WithName("CspReport")
        .WithOpenApi()
        .RequireRateLimiting("csp-report");

        return app;
    }
}
