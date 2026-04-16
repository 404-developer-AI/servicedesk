using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Export;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Api.Tickets;

public static class TicketExportEndpoints
{
    public static IEndpointRouteBuilder MapTicketExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/{id:guid}/export/pdf", async (
            Guid id, bool? excludeInternal,
            HttpContext http, ITicketRepository repo, IQueueAccessService queueAccess,
            ISlaRepository slaRepo, IMailTimelineEnricher mailEnricher,
            IAttachmentRepository attachmentRepo, IBlobStore blobStore,
            [FromServices] NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var detail = await repo.GetByIdAsync(id, ct);
            if (detail is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await queueAccess.HasQueueAccessAsync(userId, role, detail.Ticket.QueueId, ct))
                return Results.NotFound();

            // Enrich mail events with HTML body from blob storage and attachment metadata
            detail = await mailEnricher.EnrichAsync(detail, ct);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var names = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    q.name         AS queue_name,
                    s.name         AS status_name,
                    s.state_category AS status_category,
                    p.name         AS priority_name,
                    p.level        AS priority_level,
                    cat.name       AS category_name,
                    u.email        AS assignee_name,
                    c.first_name || ' ' || c.last_name AS requester_name,
                    c.email        AS requester_email,
                    co.name        AS company_name
                FROM tickets t
                LEFT JOIN queues      q   ON q.id   = t.queue_id
                LEFT JOIN statuses    s   ON s.id   = t.status_id
                LEFT JOIN priorities  p   ON p.id   = t.priority_id
                LEFT JOIN categories  cat ON cat.id = t.category_id
                LEFT JOIN users       u   ON u.id   = t.assignee_user_id
                LEFT JOIN contacts    c   ON c.id   = t.requester_contact_id
                LEFT JOIN companies   co  ON co.id  = c.company_id
                WHERE t.id = @Id
                """,
                new { Id = id });

            var slaState = await slaRepo.GetStateAsync(id, ct);

            var exporterEmail = http.User.FindFirst(ClaimTypes.Email)?.Value
                ?? http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            var exclude = excludeInternal ?? true;
            var pdfEvents = new List<TicketPdfEvent>();
            foreach (var e in detail.Events)
            {
                if (exclude && IsInternalEventType(e.EventType))
                    continue;

                var inlineImages = await LoadInlineImagesAsync(
                    e, attachmentRepo, blobStore, ct);

                pdfEvents.Add(new TicketPdfEvent(
                    e.EventType, e.AuthorName, e.BodyText, e.BodyHtml,
                    e.IsInternal, e.CreatedUtc, e.MetadataJson, inlineImages));
            }

            var pdfData = new TicketPdfData(
                Number: detail.Ticket.Number,
                Subject: detail.Ticket.Subject,
                Source: detail.Ticket.Source,
                CreatedUtc: detail.Ticket.CreatedUtc,
                UpdatedUtc: detail.Ticket.UpdatedUtc,
                DueUtc: detail.Ticket.DueUtc,
                FirstResponseUtc: detail.Ticket.FirstResponseUtc,
                ResolvedUtc: detail.Ticket.ResolvedUtc,
                ClosedUtc: detail.Ticket.ClosedUtc,
                QueueName: (string)(names.queue_name ?? "—"),
                StatusName: (string)(names.status_name ?? "—"),
                StatusCategory: (string)(names.status_category ?? "Open"),
                PriorityName: (string)(names.priority_name ?? "—"),
                PriorityLevel: (int)(names.priority_level ?? 3),
                CategoryName: (string?)names.category_name,
                AssigneeName: (string?)names.assignee_name,
                RequesterName: (string)(names.requester_name ?? "Unknown"),
                RequesterEmail: (string)(names.requester_email ?? ""),
                CompanyName: (string?)names.company_name,
                BodyText: detail.Body.BodyText,
                BodyHtml: detail.Body.BodyHtml,
                FirstResponseDeadlineUtc: slaState?.FirstResponseDeadlineUtc,
                ResolutionDeadlineUtc: slaState?.ResolutionDeadlineUtc,
                FirstResponseMetUtc: slaState?.FirstResponseMetUtc,
                ResolutionMetUtc: slaState?.ResolutionMetUtc,
                SlaPaused: slaState?.IsPaused ?? false,
                Events: pdfEvents,
                PinnedEvents: detail.PinnedEvents
                    .Select(p => new TicketPdfPin(p.EventId, p.PinnedByName, p.Remark, p.CreatedUtc))
                    .ToList(),
                ExportedAtUtc: DateTime.UtcNow,
                ExportedBy: exporterEmail,
                ServerTimezoneId: TimeZoneInfo.Local.Id);

            var pdfBytes = TicketPdfGenerator.Generate(pdfData);

            return Results.File(pdfBytes, "application/pdf", $"ticket-{detail.Ticket.Number}.pdf");
        }).WithName("ExportTicketPdf").WithOpenApi();

        return app;
    }

    private static bool IsInternalEventType(string eventType) =>
        eventType is "StatusChange" or "AssignmentChange" or "PriorityChange"
            or "QueueChange" or "CategoryChange" or "SystemNote";

    /// Loads inline image attachments for mail events from blob storage.
    /// Returns empty list for non-mail events or when no inline images exist.
    private static async Task<IReadOnlyList<TicketPdfInlineImage>> LoadInlineImagesAsync(
        Domain.Tickets.TicketEvent evt,
        IAttachmentRepository attachmentRepo,
        IBlobStore blobStore,
        CancellationToken ct)
    {
        if (evt.EventType is not ("MailReceived" or "Mail"))
            return [];

        // Extract mail_message_id from metadata
        Guid? mailId = null;
        if (!string.IsNullOrWhiteSpace(evt.MetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(evt.MetadataJson);
                if (doc.RootElement.TryGetProperty("mail_message_id", out var prop)
                    && prop.ValueKind == JsonValueKind.String
                    && Guid.TryParse(prop.GetString(), out var parsed))
                {
                    mailId = parsed;
                }
            }
            catch { /* metadata unparseable */ }
        }

        if (!mailId.HasValue) return [];

        var attachments = await attachmentRepo.ListByMailAsync(mailId.Value, ct);
        var inlineImages = new List<TicketPdfInlineImage>();

        foreach (var att in attachments)
        {
            if (!att.IsInline || att.ProcessingState != "Ready")
                continue;
            if (string.IsNullOrEmpty(att.ContentHash))
                continue;
            if (!att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                await using var stream = await blobStore.OpenReadAsync(att.ContentHash, ct);
                if (stream is null) continue;

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                inlineImages.Add(new TicketPdfInlineImage(
                    att.OriginalFilename, att.MimeType, ms.ToArray()));
            }
            catch
            {
                // Skip unreadable blobs — don't fail the entire export
            }
        }

        return inlineImages;
    }
}
