using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Mail.Polling;

namespace Servicedesk.Api.Health;

/// Three endpoints implement the three-layer health flow:
///   • GET  /api/system/health            — public status-only rollup (dashboard pill)
///   • GET  /api/admin/health             — full per-subsystem detail (admin page)
///   • POST /api/admin/health/mail-polling/queues/{id}/reset — retry action
/// Non-admins never see error messages — only the rollup colour.
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/health", async (IHealthAggregator agg, CancellationToken ct) =>
        {
            var report = await agg.CollectAsync(ct);
            return Results.Ok(new { status = report.Status.ToString() });
        })
        .WithName("GetSystemHealth").WithOpenApi();

        var admin = app.MapGroup("/api/admin/health")
            .WithTags("Health")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("", async (IHealthAggregator agg, CancellationToken ct) =>
        {
            var report = await agg.CollectAsync(ct);
            return Results.Ok(new
            {
                status = report.Status.ToString(),
                subsystems = report.Subsystems.Select(s => new
                {
                    key = s.Key,
                    label = s.Label,
                    status = s.Status.ToString(),
                    summary = s.Summary,
                    details = s.Details.Select(d => new { label = d.Label, value = d.Value }),
                    actions = s.Actions.Select(a => new
                    {
                        key = a.Key,
                        label = a.Label,
                        endpoint = a.Endpoint,
                        confirmMessage = a.ConfirmMessage,
                    }),
                }),
            });
        })
        .WithName("GetAdminHealth").WithOpenApi();

        admin.MapPost("/mail-polling/queues/{queueId:guid}/reset", async (
            Guid queueId,
            HttpContext http,
            IMailPollStateRepository pollState,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            await pollState.ResetFailuresAsync(queueId, ct);
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "health.mail-polling.reset",
                Actor: actor,
                ActorRole: role,
                Target: queueId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { queueId }));
            return Results.NoContent();
        })
        .WithName("ResetMailPollingFailures").WithOpenApi();

        return app;
    }
}
