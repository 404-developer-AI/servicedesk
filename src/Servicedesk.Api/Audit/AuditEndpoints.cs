using System.Text.Json;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using InfraAuditQuery = Servicedesk.Infrastructure.Audit.AuditQuery;

namespace Servicedesk.Api.Audit;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit");

        group.MapGet("/", async (
            HttpContext ctx,
            IAuditQuery query,
            string? eventType,
            string? actor,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            long? cursor,
            int? limit,
            CancellationToken ct) =>
        {
            var forbidden = DevRoleGate.RequireAdmin(ctx);
            if (forbidden is not null) return forbidden;

            var q = new InfraAuditQuery(
                EventType: eventType,
                Actor: actor,
                FromUtc: fromUtc,
                ToUtc: toUtc,
                CursorId: cursor,
                Limit: limit ?? 50);

            var page = await query.ListAsync(q, ct);
            return Results.Ok(new
            {
                items = page.Items.Select(Project),
                nextCursor = page.NextCursor,
            });
        })
        .WithName("ListAuditEntries")
        .WithOpenApi();

        group.MapGet("/{id:long}", async (HttpContext ctx, long id, IAuditQuery query, CancellationToken ct) =>
        {
            var forbidden = DevRoleGate.RequireAdmin(ctx);
            if (forbidden is not null) return forbidden;

            var entry = await query.GetAsync(id, ct);
            return entry is null ? Results.NotFound() : Results.Ok(Project(entry));
        })
        .WithName("GetAuditEntry")
        .WithOpenApi();

        return app;
    }

    private static object Project(AuditLogEntry e) => new
    {
        id = e.Id,
        utc = DateTime.SpecifyKind(e.Utc, DateTimeKind.Utc),
        actor = e.Actor,
        actorRole = e.ActorRole,
        eventType = e.EventType,
        target = e.Target,
        clientIp = e.ClientIp,
        userAgent = e.UserAgent,
        payload = SafeParse(e.PayloadJson),
        entryHash = Convert.ToHexString(e.EntryHash),
        prevHash = Convert.ToHexString(e.PrevHash),
    };

    private static JsonElement SafeParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }
}
