using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Claude;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Api.Tickets;

/// In-ticket "Ask AI for a proposal" endpoint. Agent+Admin only, queue-scoped
/// like every other ticket surface. The actual scoping, budget enforcement and
/// usage logging live in <see cref="IClaudeAssistService"/>; this endpoint only
/// authorizes the caller against the ticket's queue, forwards the agent's
/// screenshot selection, and maps the result to HTTP.
public static class TicketClaudeEndpoints
{
    /// Defensive cap on how many attachment ids a client may submit; the
    /// service additionally caps how many images it actually sends.
    private const int MaxAttachmentIds = 50;

    public static IEndpointRouteBuilder MapTicketClaudeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapPost("/{id:guid}/ai-proposal", CreateProposal)
            .WithName("CreateTicketAiProposal").WithOpenApi();

        // Lists the ticket's image attachments so the agent can tick which
        // screenshots to send. Same queue-scoping as the proposal call.
        group.MapGet("/{id:guid}/ai-proposal/images", ListImages)
            .WithName("ListTicketAiProposalImages").WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListImages(
        Guid id,
        HttpContext http,
        ITicketRepository tickets,
        IQueueAccessService queueAccess,
        [FromServices] Npgsql.NpgsqlDataSource dataSource,
        CancellationToken ct)
    {
        var ticket = await tickets.GetByIdAsync(id, ct);
        if (ticket is null) return Results.NotFound();

        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
        if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
            return Results.NotFound();

        // Three ownership paths surface a ticket's images:
        //  (a) agent uploads staged directly on the ticket (owner Ticket, no event)
        //  (b) attachments on a Note/Comment/MailSent event of this ticket
        //  (c) inbound-mail attachments — including inline images pasted into a
        //      mail body — owned by a mail_messages row linked to this ticket.
        // (c) is the common case for "I emailed a screenshot": those are
        //     owner_kind='Mail', so without this branch the picker is empty and
        //     the image never reaches the model.
        const string sql = """
            SELECT a.id                AS Id,
                   a.original_filename AS Filename,
                   a.mime_type         AS MimeType,
                   a.size_bytes        AS SizeBytes,
                   a.owner_kind        AS OwnerKind,
                   a.owner_id          AS OwnerId,
                   a.is_inline         AS IsInline
              FROM attachments a
              LEFT JOIN ticket_events e ON e.id = a.event_id
              LEFT JOIN mail_messages m ON a.owner_kind = 'Mail' AND m.id = a.owner_id
             WHERE a.mime_type ILIKE 'image/%'
               AND a.processing_state = 'Ready'
               AND (
                     (a.owner_kind = 'Ticket' AND a.owner_id = @id AND a.event_id IS NULL)
                  OR (a.event_id IS NOT NULL AND e.ticket_id = @id)
                  OR (a.owner_kind = 'Mail' AND m.ticket_id = @id)
                   )
             ORDER BY a.created_utc ASC
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await Dapper.SqlMapper.QueryAsync<ImageRow>(conn,
            new Dapper.CommandDefinition(sql, new { id }, cancellationToken: ct));
        return Results.Ok(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                filename = string.IsNullOrWhiteSpace(r.Filename) ? "image" : r.Filename,
                mimeType = r.MimeType,
                sizeBytes = r.SizeBytes,
                isInline = r.IsInline,
                // Mail-owned images live behind the per-mail download route;
                // ticket/event-owned images behind the generic one.
                url = r.OwnerKind == "Mail"
                    ? $"/api/tickets/{id}/mail/{r.OwnerId}/attachments/{r.Id}?inline=true"
                    : $"/api/tickets/{id}/attachments/{r.Id}?inline=true",
            }),
        });
    }

    private sealed class ImageRow
    {
        public Guid Id { get; set; }
        public string Filename { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string OwnerKind { get; set; } = string.Empty;
        public Guid OwnerId { get; set; }
        public bool IsInline { get; set; }
    }

    public sealed record ProposalRequest(IReadOnlyList<Guid>? AttachmentIds);

    private static async Task<IResult> CreateProposal(
        Guid id,
        [FromBody] ProposalRequest? req,
        HttpContext http,
        ITicketRepository tickets,
        IQueueAccessService queueAccess,
        ITaxonomyRepository taxonomy,
        IClaudeAssistService assist,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var ticket = await tickets.GetByIdAsync(id, ct);
        if (ticket is null) return Results.NotFound();

        var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
        if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
            return Results.NotFound();

        // Per-queue availability gate. The button is hidden client-side when a
        // queue has AI assist switched off, but the same check is enforced here
        // so the call can't be replayed against a disabled queue.
        var queue = await taxonomy.GetQueueAsync(ticket.Ticket.QueueId, ct);
        if (queue is null || !queue.AiAssistEnabled)
        {
            await WriteAuditAsync(audit, http, id, "queue_disabled", ct);
            return Results.Json(new
            {
                error = "queue_disabled",
                message = "The AI assistant is not enabled for this ticket's queue.",
            }, statusCode: 409);
        }

        var attachmentIds = (req?.AttachmentIds ?? Array.Empty<Guid>())
            .Distinct()
            .Take(MaxAttachmentIds)
            .ToList();

        ClaudeProposalResult result;
        try
        {
            result = await assist.GenerateProposalAsync(id, userId, attachmentIds, ct);
        }
        catch (ClaudeApiException ex)
        {
            await WriteAuditAsync(audit, http, id, "error", ct);
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await WriteAuditAsync(audit, http, id, result.Outcome.ToString(), ct);

        return result.Outcome switch
        {
            ClaudeProposalOutcome.Ok => Results.Ok(new
            {
                proposalText = result.ProposalText,
                proposalHtml = result.ProposalHtml,
                inputTokens = result.InputTokens,
                outputTokens = result.OutputTokens,
                costMicroEur = result.CostMicroEur,
                imageCount = result.ImageCount,
                monthSpendMicroEur = result.MonthSpendMicroEur,
                monthBudgetMicroEur = result.MonthBudgetMicroEur,
            }),
            ClaudeProposalOutcome.Refused => Results.Ok(new
            {
                refused = true,
                message = result.Message,
                monthSpendMicroEur = result.MonthSpendMicroEur,
                monthBudgetMicroEur = result.MonthBudgetMicroEur,
            }),
            ClaudeProposalOutcome.NoBudget or ClaudeProposalOutcome.BudgetExceeded => Results.Json(new
            {
                error = result.Outcome == ClaudeProposalOutcome.NoBudget ? "no_budget" : "budget_exceeded",
                message = result.Message,
                monthSpendMicroEur = result.MonthSpendMicroEur,
                monthBudgetMicroEur = result.MonthBudgetMicroEur,
            }, statusCode: 409),
            ClaudeProposalOutcome.Disabled => Results.Json(new { error = "disabled", message = result.Message }, statusCode: 409),
            ClaudeProposalOutcome.NotConfigured => Results.Json(new { error = "not_configured", message = result.Message }, statusCode: 409),
            ClaudeProposalOutcome.ZdrNotConfirmed => Results.Json(new { error = "zdr_not_confirmed", message = result.Message }, statusCode: 409),
            _ => Results.Json(new { error = "unknown", message = result.Message }, statusCode: 409),
        };
    }

    private static async Task WriteAuditAsync(
        IAuditLogger audit, HttpContext http, Guid ticketId, string outcome, CancellationToken ct)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: ClaudeEventTypes.ProposalRequested,
            Actor: actor,
            ActorRole: role,
            Target: $"ticket:{ticketId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { ticketId, outcome }), ct);
    }
}
