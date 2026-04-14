using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Health;
using System.Security.Claims;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Storage;

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

        admin.MapPost("/attachment-jobs/requeue-dead-lettered", async (
            HttpContext http,
            IAttachmentJobRepository jobs,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var requeued = await jobs.RequeueDeadLetteredAsync(DateTime.UtcNow, ct);
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "health.attachment-jobs.requeue",
                Actor: actor,
                ActorRole: role,
                Target: "dead-lettered",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { requeued }));
            return Results.Ok(new { requeued });
        })
        .WithName("RequeueDeadLetteredAttachmentJobs").WithOpenApi();

        admin.MapPost("/blob-store/clear", async (
            HttpContext http,
            IBlobStoreHealth blobHealth,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            blobHealth.Clear();
            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "health.blob-store.clear",
                Actor: actor,
                ActorRole: role,
                Target: "blob-store",
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: null));
            return Results.NoContent();
        })
        .WithName("ClearBlobStoreHealth").WithOpenApi();

        admin.MapGet("/incidents", async (
            IIncidentLog incidents,
            int? take,
            CancellationToken ct) =>
        {
            var rows = await incidents.ListRecentAsync(take ?? 200, ct);
            return Results.Ok(new
            {
                items = rows.Select(r => new
                {
                    id = r.Id,
                    subsystem = r.Subsystem,
                    severity = r.Severity.ToString(),
                    message = r.Message,
                    details = r.Details,
                    firstOccurredUtc = r.FirstOccurredUtc,
                    lastOccurredUtc = r.LastOccurredUtc,
                    occurrenceCount = r.OccurrenceCount,
                    acknowledgedUtc = r.AcknowledgedUtc,
                    acknowledgedByUserId = r.AcknowledgedByUserId,
                }),
            });
        })
        .WithName("ListIncidents").WithOpenApi();

        admin.MapPost("/incidents/{id:long}/ack", async (
            long id,
            HttpContext http,
            IIncidentLog incidents,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var (actor, role) = ActorContext.Resolve(http);
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var ok = await incidents.AcknowledgeAsync(id, userId, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: "health.incidents.ack",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { id, acknowledged = ok }));
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("AcknowledgeIncident").WithOpenApi();

        admin.MapPost("/incidents/ack-subsystem/{subsystem}", async (
            string subsystem,
            HttpContext http,
            IIncidentLog incidents,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var (actor, role) = ActorContext.Resolve(http);
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var count = await incidents.AcknowledgeSubsystemAsync(subsystem, userId, ct);
            await audit.LogAsync(new AuditEvent(
                EventType: "health.incidents.ack-subsystem",
                Actor: actor,
                ActorRole: role,
                Target: subsystem,
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { subsystem, count }));
            return Results.Ok(new { acknowledged = count });
        })
        .WithName("AcknowledgeIncidentsBySubsystem").WithOpenApi();

        return app;
    }
}
