using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Activity;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.KnowledgeBase;

/// Shared audit-write helper for the KB endpoints. Mirrors the inline
/// helper used by other endpoint files (CompanyEndpoints, TicketEndpoints)
/// but lives in a single place because the KB surface spans multiple files.
///
/// v0.0.42 — every call also mirrors a row into the agent activity feed
/// via <see cref="IActivityRecorder"/>, resolved from the HTTP request
/// services so existing call sites do not need a new parameter.
internal static class KbAudit
{
    public static async Task WriteAsync(
        IAuditLogger audit, HttpContext http, string eventType, string target, object payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType, Actor: actor, ActorRole: role, Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));

        var recorder = http.RequestServices.GetService<IActivityRecorder>();
        if (recorder is null) return;

        var idClaim = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idClaim, out var agentId)) return;

        await recorder.RecordAsync(new ActivityRecord(
            AgentId: agentId,
            EventType: eventType,
            Summary: BuildSummary(eventType),
            EntityType: "kb_article",
            EntityId: target,
            Metadata: payload));
    }

    private static string BuildSummary(string eventType) => eventType switch
    {
        "kb.article.created" => "created KB article",
        "kb.article.updated" => "edited KB article",
        "kb.article.status.changed" => "changed KB article status",
        "kb.article.featured.set" => "featured KB article",
        "kb.article.featured.unset" => "unfeatured KB article",
        "kb.article.moved" => "moved KB article",
        "kb.article.deleted" => "deleted KB article",
        "kb.article.attachment.added" => "added KB attachment",
        "kb.section.created" => "created KB section",
        "kb.section.updated" => "edited KB section",
        "kb.section.deleted" => "deleted KB section",
        _ => "KB activity",
    };
}
