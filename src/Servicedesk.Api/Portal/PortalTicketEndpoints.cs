using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tickets;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;
using Servicedesk.Infrastructure.Triggers;

namespace Servicedesk.Api.Portal;

/// v0.1.0 — what a signed-in customer can do with tickets. Every handler
/// resolves the viewer (contact / company / role) from the session and
/// runs scope-checked queries; a ticket outside the scope is a 404, never a
/// 403. The projection is a whitelist: internal notes, checklists, project
/// tickets, timesheet data, queue/agent internals and non-whitelisted
/// timeline events never leave the server.
public static class PortalTicketEndpoints
{
    private const string CanonicalPrefix = "/api/tickets/";
    private const string PortalPrefix = "/api/portal/tickets/";

    public static IEndpointRouteBuilder MapPortalTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal/tickets")
            .WithTags("PortalTickets")
            .RequireAuthorization(AuthorizationPolicies.RequireCustomer);

        group.MapGet("/", List).WithName("PortalListTickets").WithOpenApi();
        group.MapPost("/", Create).WithName("PortalCreateTicket").WithOpenApi();
        group.MapGet("/{id:guid}", Detail).WithName("PortalTicketDetail").WithOpenApi();
        group.MapPost("/{id:guid}/messages", Reply).WithName("PortalReply").WithOpenApi();
        group.MapPost("/{id:guid}/attachments", Upload).WithName("PortalUploadAttachment").WithOpenApi().DisableRequestTimeout();
        group.MapGet("/{id:guid}/attachments/{attachmentId:guid}", DownloadEventAttachment).WithName("PortalGetAttachment").WithOpenApi();
        group.MapGet("/{id:guid}/mail/{mailMessageId:guid}/attachments/{attachmentId:guid}", DownloadMailAttachment).WithName("PortalGetMailAttachment").WithOpenApi();
        return app;
    }

    /// Resolves the active viewer or null (portal disabled / account no
    /// longer Active / no contact link). Callers answer 404 on null.
    private static async Task<PortalViewer?> ViewerAsync(HttpContext http, IPortalAccountRepository accounts, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return null;
        var userId = PortalRequest.UserId(http);
        if (userId is null) return null;
        var viewer = await accounts.GetViewerAsync(userId.Value, ct);
        if (viewer is null || viewer.Status != PortalAccountStatus.Active || viewer.ContactId is null) return null;
        return viewer;
    }

    // ---- list -------------------------------------------------------------

    private static async Task<IResult> List(
        HttpContext http, [FromQuery] string? filter, [FromQuery] string? search, [FromQuery] int? page,
        [FromQuery] Guid? companyId,
        IPortalAccountRepository accounts, IPortalTicketRepository tickets, ISettingsService settings, CancellationToken ct)
    {
        var viewer = await ViewerAsync(http, accounts, settings, ct);
        if (viewer is null) return PortalRequest.Disabled();
        // One company at a time. No companyId → the default (primary) one;
        // a companyId the viewer is not linked to → 404 (no existence leak).
        var active = companyId is null ? viewer.DefaultCompany : viewer.Company(companyId.Value);
        if (companyId is not null && active is null) return Results.NotFound();
        var f = (filter ?? "open").ToLowerInvariant() switch
        {
            "closed" => PortalTicketFilter.Closed,
            "all" => PortalTicketFilter.All,
            _ => PortalTicketFilter.Open,
        };
        var pageSize = await settings.GetAsync<int>(SettingKeys.Portal.TicketPageSize, ct);
        if (pageSize <= 0) pageSize = 25;
        var term = search is { Length: > 200 } ? search[..200] : search;
        var result = await tickets.ListAsync(viewer, active?.CompanyId, f, term, page ?? 1, pageSize, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(t => new
            {
                id = t.Id,
                number = t.Number,
                subject = t.Subject,
                status = new { name = t.StatusName, color = t.StatusColor, category = t.StateCategory },
                priority = new { name = t.PriorityName, color = t.PriorityColor, level = t.PriorityLevel },
                requester = new
                {
                    name = $"{t.RequesterFirstName} {t.RequesterLastName}".Trim(),
                    email = t.RequesterEmail,
                    isYou = t.RequesterContactId == viewer.ContactId,
                },
                createdUtc = t.CreatedUtc,
                updatedUtc = t.UpdatedUtc,
                closedUtc = t.ClosedUtc,
            }),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
            companyId = active?.CompanyId,
            companyName = active?.CompanyName,
            scope = active?.IsTicketManager == true ? "company" : "own",
        });
    }

    // ---- detail -----------------------------------------------------------

    private static async Task<IResult> Detail(
        Guid id, HttpContext http,
        IPortalAccountRepository accounts, IPortalTicketRepository portalTickets, ITicketRepository tickets,
        IMailTimelineEnricher enricher, ISettingsService settings, CancellationToken ct)
    {
        var viewer = await ViewerAsync(http, accounts, settings, ct);
        if (viewer is null) return PortalRequest.Disabled();
        var header = await portalTickets.GetHeaderAsync(viewer, id, ct);
        if (header is null) return Results.NotFound();

        var detail = await tickets.GetByIdAsync(id, ct);
        if (detail is null) return Results.NotFound();
        detail = await enricher.EnrichAsync(detail, ct);

        var messages = detail.Events
            .Where(e => !e.IsInternal && PortalTicketRepository.CustomerVisibleEventTypes.Contains(e.EventType))
            .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id)
            .Select(e => ProjectEvent(e, id, viewer))
            .Where(m => m is not null)
            .ToList();

        var allowResolvedReply = await settings.GetAsync<bool>(SettingKeys.Portal.AllowReplyOnResolved, ct);
        var (canReply, reason) = ReplyGate(header.StateCategory, allowResolvedReply);

        return Results.Ok(new
        {
            ticket = new
            {
                id = header.Id,
                number = header.Number,
                subject = header.Subject,
                status = new { name = header.StatusName, color = header.StatusColor, category = header.StateCategory },
                priority = new { name = header.PriorityName, color = header.PriorityColor },
                requester = new
                {
                    name = $"{header.RequesterFirstName} {header.RequesterLastName}".Trim(),
                    email = header.RequesterEmail,
                    isYou = header.RequesterContactId == viewer.ContactId,
                },
                companyId = header.CompanyId,
                companyName = header.CompanyName,
                source = header.Source,
                createdUtc = header.CreatedUtc,
                updatedUtc = header.UpdatedUtc,
                resolvedUtc = header.ResolvedUtc,
                closedUtc = header.ClosedUtc,
                descriptionHtml = RewriteUrls(header.BodyHtml, id),
                descriptionText = header.BodyText,
            },
            messages,
            canReply,
            replyBlockedReason = reason,
        });
    }

    private static (bool CanReply, string? Reason) ReplyGate(string stateCategory, bool allowResolved)
    {
        if (stateCategory == "Closed") return (false, "closed");
        if (stateCategory == "Resolved" && !allowResolved) return (false, "resolved");
        return (true, null);
    }

    /// Whitelisted projection of one timeline event. Agent identities are
    /// never exposed by email: an agent article shows as "Support team".
    private static object? ProjectEvent(TicketEvent e, Guid ticketId, PortalViewer viewer)
    {
        var kind = e.AuthorUserId.HasValue ? "agent" : e.AuthorContactId.HasValue ? "customer" : "system";
        var attachments = new List<object>();
        string? statusFrom = null, statusTo = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(e.MetadataJson) ? "{}" : e.MetadataJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in atts.EnumerateArray())
                {
                    var url = a.TryGetProperty("url", out var u) ? u.GetString() : null;
                    attachments.Add(new
                    {
                        id = a.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                        name = a.TryGetProperty("name", out var n) ? n.GetString() : null,
                        mimeType = a.TryGetProperty("mimeType", out var m) ? m.GetString() : null,
                        size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var szv) ? szv : 0,
                        url = RewriteUrls(url, ticketId),
                    });
                }
            }
            if (e.EventType == "StatusChange")
            {
                statusFrom = root.TryGetProperty("fromName", out var f) ? f.GetString() : null;
                statusTo = root.TryGetProperty("toName", out var t) ? t.GetString() : null;
                if (statusTo is null) return null;
            }
        }
        catch (JsonException)
        {
            // Malformed metadata: render the body without extras.
        }

        var authorName = kind switch
        {
            "customer" => e.AuthorContactId == viewer.ContactId ? "You" : (e.AuthorName ?? "Customer"),
            "agent" => "Support team",
            _ => "System",
        };

        return new
        {
            id = e.Id,
            type = e.EventType,
            kind,
            authorName,
            isYou = kind == "customer" && e.AuthorContactId == viewer.ContactId,
            bodyHtml = e.EventType == "StatusChange" ? null : RewriteUrls(e.BodyHtml, ticketId),
            bodyText = e.EventType == "StatusChange" ? null : e.BodyText,
            statusChange = e.EventType == "StatusChange" ? new { from = statusFrom, to = statusTo } : null,
            attachments,
            createdUtc = e.CreatedUtc,
        };
    }

    /// The enricher emits agent-side attachment URLs; the portal serves the
    /// same bytes through its own scope-checked endpoints.
    private static string? RewriteUrls(string? html, Guid ticketId)
    {
        if (string.IsNullOrEmpty(html)) return html;
        return html.Replace(CanonicalPrefix + ticketId + "/", PortalPrefix + ticketId + "/", StringComparison.Ordinal);
    }

    // ---- create -----------------------------------------------------------

    public sealed record CreateRequest([property: Required] string Subject, string? BodyHtml, Guid? CompanyId);

    private static async Task<IResult> Create(
        [FromBody] CreateRequest req, HttpContext http,
        IPortalAccountRepository accounts, ITicketRepository tickets, ITaxonomyRepository taxonomy,
        IContactLookupService contactLookup, ISlaEngine sla, ITicketListNotifier notifier, ITriggerService triggers,
        ISettingsService settings, IAuditLogger audit, CancellationToken ct)
    {
        if (PortalRequest.IsImpersonated(http)) return PortalRequest.ReadOnly();
        var viewer = await ViewerAsync(http, accounts, settings, ct);
        if (viewer is null) return PortalRequest.Disabled();

        var queueRaw = await settings.GetAsync<string>(SettingKeys.Portal.NewTicketQueueId, ct);
        if (!Guid.TryParse(queueRaw, out var queueId) || await taxonomy.GetQueueAsync(queueId, ct) is null)
            return Results.Json(new { error = "creation_disabled", message = "Creating tickets from the portal is not available. Reply to an existing ticket or contact the service desk by mail." }, statusCode: StatusCodes.Status403Forbidden);

        var subject = (req.Subject ?? string.Empty).Trim();
        if (subject.Length is 0 or > 300)
            return Results.BadRequest(new { error = "invalid_subject", message = "Enter a subject (max 300 characters)." });
        var bodyHtml = PortalHtmlSanitizer.Sanitize(req.BodyHtml);
        var bodyText = KbBodyStripper.HtmlToText(bodyHtml);
        if (bodyText.Trim().Length == 0)
            return Results.BadRequest(new { error = "empty_body", message = "Describe your request." });
        if (bodyHtml.Length > 200_000)
            return Results.BadRequest(new { error = "body_too_long", message = "The description is too long." });

        var statuses = await taxonomy.ListStatusesAsync(ct);
        var priorities = await taxonomy.ListPrioritiesAsync(ct);
        var status = statuses.FirstOrDefault(s => s.IsDefault && s.IsActive) ?? statuses.FirstOrDefault(s => s.IsActive);
        var priority = priorities.FirstOrDefault(p => p.IsDefault && p.IsActive) ?? priorities.FirstOrDefault(p => p.IsActive);
        if (status is null || priority is null)
            return Results.Json(new { error = "taxonomy_missing" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        // The ticket is opened for the customer's active company (must be
        // one of their links); without any company the usual resolution
        // for the contact applies.
        PortalCompanyAccess? forCompany = null;
        if (req.CompanyId is { } requested)
        {
            forCompany = viewer.Company(requested);
            if (forCompany is null)
                return Results.BadRequest(new { error = "invalid_company", message = "You have no access to that company." });
        }
        else
        {
            forCompany = viewer.DefaultCompany;
        }
        var resolution = forCompany is not null
            ? new CompanyResolution(forCompany.CompanyId, "manual", false)
            : await contactLookup.ResolveCompanyForNewTicketAsync(viewer.ContactId!.Value, ct);
        var created = await tickets.CreateAsync(new NewTicket(
            Subject: subject,
            BodyText: bodyText,
            BodyHtml: bodyHtml,
            RequesterContactId: viewer.ContactId.Value,
            QueueId: queueId,
            StatusId: status.Id,
            PriorityId: priority.Id,
            CategoryId: null,
            AssigneeUserId: null,
            Source: TicketSource.Portal.ToString(),
            CompanyId: resolution.CompanyId,
            AwaitingCompanyAssignment: resolution.Awaiting,
            CompanyResolvedVia: resolution.ResolvedVia), ct);

        // The description is also the first customer article so the
        // timeline (agent + portal) shows who said what, mirroring mail.
        var evt = await tickets.AddEventAsync(created.Id, new NewTicketEvent(
            EventType: TicketEventType.PortalMessage.ToString(),
            BodyText: bodyText,
            BodyHtml: bodyHtml,
            IsInternal: false,
            AuthorUserId: null,
            AuthorContactId: viewer.ContactId,
            MetadataJson: JsonSerializer.Serialize(new { source = "portal", initial = true })), ct);

        await audit.LogAsync(new AuditEvent(PortalEventTypes.TicketCreated, viewer.Email, "Customer",
            Target: created.Id.ToString(), ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { number = created.Number, queueId, contactId = viewer.ContactId, companyId = resolution.CompanyId }), ct);

        await sla.OnTicketCreatedAsync(created.Id, ct);
        await notifier.NotifyUpdatedAsync(created.Id, ct);
        // Same activation as a brand-new inbound mail: article-only change
        // set flagged as creation so "ticket.is_new + article.sender=Customer"
        // triggers fire.
        await triggers.EvaluateAsync(created.Id, evt?.Id, TriggerActivatorKind.Action,
            TriggerChangeSet.ArticleOnly(isTicketCreation: true), ct);

        return Results.Created($"/api/portal/tickets/{created.Id}", new
        {
            id = created.Id,
            number = created.Number,
            messageEventId = evt?.Id,
        });
    }

    // ---- reply ------------------------------------------------------------

    public sealed record ReplyRequest([property: Required] string BodyHtml);

    private static async Task<IResult> Reply(
        Guid id, [FromBody] ReplyRequest req, HttpContext http,
        IPortalAccountRepository accounts, IPortalTicketRepository portalTickets, ITicketRepository tickets,
        ISlaEngine sla, ITicketListNotifier notifier, ITriggerService triggers,
        ISettingsService settings, IAuditLogger audit, CancellationToken ct)
    {
        if (PortalRequest.IsImpersonated(http)) return PortalRequest.ReadOnly();
        var viewer = await ViewerAsync(http, accounts, settings, ct);
        if (viewer is null) return PortalRequest.Disabled();
        var header = await portalTickets.GetHeaderAsync(viewer, id, ct);
        if (header is null) return Results.NotFound();

        var allowResolvedReply = await settings.GetAsync<bool>(SettingKeys.Portal.AllowReplyOnResolved, ct);
        var (canReply, reason) = ReplyGate(header.StateCategory, allowResolvedReply);
        if (!canReply)
            return Results.Conflict(new { error = "reply_blocked", reason, message = reason == "closed"
                ? "This ticket is closed. Create a new ticket instead."
                : "This ticket is resolved and no longer accepts replies. Create a new ticket instead." });

        var bodyHtml = PortalHtmlSanitizer.Sanitize(req.BodyHtml);
        var bodyText = KbBodyStripper.HtmlToText(bodyHtml);
        if (bodyText.Trim().Length == 0)
            return Results.BadRequest(new { error = "empty_body", message = "Write a message." });
        if (bodyHtml.Length > 200_000)
            return Results.BadRequest(new { error = "body_too_long", message = "The message is too long." });

        var evt = await tickets.AddEventAsync(id, new NewTicketEvent(
            EventType: TicketEventType.PortalMessage.ToString(),
            BodyText: bodyText,
            BodyHtml: bodyHtml,
            IsInternal: false,
            AuthorUserId: null,
            AuthorContactId: viewer.ContactId,
            MetadataJson: JsonSerializer.Serialize(new { source = "portal" })), ct);
        if (evt is null) return Results.NotFound();

        await audit.LogAsync(new AuditEvent(PortalEventTypes.TicketReplied, viewer.Email, "Customer",
            Target: id.ToString(), ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { eventId = evt.Id, contactId = viewer.ContactId }), ct);

        await sla.OnTicketEventAsync(id, evt.EventType, ct);
        await notifier.NotifyUpdatedAsync(id, ct);
        await triggers.EvaluateAsync(id, evt.Id, TriggerActivatorKind.Action, TriggerChangeSet.ArticleOnly(), ct);

        return Results.Created($"/api/portal/tickets/{id}", new { eventId = evt.Id });
    }

    // ---- attachments ------------------------------------------------------

    private static async Task<IResult> Upload(
        Guid id, [FromQuery] long? eventId, HttpContext http,
        IPortalAccountRepository accounts, IPortalTicketRepository portalTickets,
        IAttachmentRepository attachments, IBlobStore blobs, ISettingsService settings, IAuditLogger audit,
        CancellationToken ct)
    {
        if (PortalRequest.IsImpersonated(http)) return PortalRequest.ReadOnly();
        var viewer = await ViewerAsync(http, accounts, settings, ct);
        if (viewer is null) return PortalRequest.Disabled();
        var header = await portalTickets.GetHeaderAsync(viewer, id, ct);
        if (header is null) return Results.NotFound();
        // Files only ever attach to a portal message the viewer wrote.
        if (eventId is null || !await portalTickets.PortalMessageBelongsToContactAsync(id, eventId.Value, viewer.ContactId!.Value, ct))
            return Results.NotFound();

        var stored = await TicketAttachmentEndpoints.StoreUploadedFileAsync(http, id, attachments, blobs, settings, ct);
        if (stored.Error is not null) return stored.Error;
        var (attachmentId, filename, mimeType, write) = stored.Ok!.Value;

        var moved = await attachments.ReassignToEventAsync(new[] { attachmentId }, id, eventId.Value, ct);
        if (moved != 1) return Results.NotFound();

        await audit.LogAsync(new AuditEvent(PortalEventTypes.AttachmentUploaded, viewer.Email, "Customer",
            Target: attachmentId.ToString(), ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { ticketId = id, eventId, filename, mimeType, size = write.SizeBytes }), ct);

        return Results.Created($"{PortalPrefix}{id}/attachments/{attachmentId}", new
        {
            id = attachmentId,
            url = $"{PortalPrefix}{id}/attachments/{attachmentId}",
            mimeType,
            size = write.SizeBytes,
            filename,
        });
    }

    private static async Task<IResult> DownloadEventAttachment(
        Guid id, Guid attachmentId, [FromQuery] bool? inline, HttpContext http,
        IPortalAccountRepository accounts, IPortalTicketRepository portalTickets,
        IAttachmentRepository attachments, IBlobStore blobs, ISettingsService settings, IAuditLogger audit,
        CancellationToken ct)
    {
        var viewer = await ViewerAsync(http, accounts, settings, ct);
        if (viewer is null) return PortalRequest.Disabled();
        var header = await portalTickets.GetHeaderAsync(viewer, id, ct);
        if (header is null) return Results.NotFound();

        var att = await attachments.GetByIdAsync(attachmentId, ct);
        if (att is null || att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash)) return Results.NotFound();
        // Only event-attached rows whose event is customer-visible. Staged
        // (event-less) uploads are agent drafts and stay hidden.
        if (att.EventId is null || !await portalTickets.EventIsCustomerVisibleAsync(id, att.EventId.Value, ct))
            return Results.NotFound();

        return await ServeAsync(http, att.ContentHash, att.MimeType, att.OriginalFilename, inline == true, blobs, audit,
            viewer, new { ticketId = id, eventId = att.EventId, attachmentId }, ct);
    }

    private static async Task<IResult> DownloadMailAttachment(
        Guid id, Guid mailMessageId, Guid attachmentId, [FromQuery] bool? inline, HttpContext http,
        IPortalAccountRepository accounts, IPortalTicketRepository portalTickets,
        IAttachmentRepository attachments, IMailMessageRepository mail, IBlobStore blobs,
        ISettingsService settings, IAuditLogger audit, CancellationToken ct)
    {
        var viewer = await ViewerAsync(http, accounts, settings, ct);
        if (viewer is null) return PortalRequest.Disabled();
        var header = await portalTickets.GetHeaderAsync(viewer, id, ct);
        if (header is null) return Results.NotFound();

        var att = await attachments.GetByIdAsync(attachmentId, ct);
        if (att is null || att.OwnerKind != "Mail" || att.OwnerId != mailMessageId) return Results.NotFound();
        if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash)) return Results.NotFound();
        var mailRow = await mail.GetByIdAsync(mailMessageId, ct);
        if (mailRow is null || mailRow.TicketId != id) return Results.NotFound();
        if (!await portalTickets.MailMessageIsCustomerVisibleAsync(id, mailMessageId, ct)) return Results.NotFound();

        return await ServeAsync(http, att.ContentHash, att.MimeType, att.OriginalFilename, inline == true, blobs, audit,
            viewer, new { ticketId = id, mailMessageId, attachmentId }, ct);
    }

    private static async Task<IResult> ServeAsync(
        HttpContext http, string contentHash, string? mimeType, string? originalFilename, bool inline,
        IBlobStore blobs, IAuditLogger audit, PortalViewer viewer, object auditPayload, CancellationToken ct)
    {
        var etag = $"\"{contentHash}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, max-age=604800, must-revalidate";
        var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag)))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var stream = await blobs.OpenReadAsync(contentHash, ct);
        if (stream is null) return Results.NotFound();

        await audit.LogAsync(new AuditEvent(PortalEventTypes.AttachmentViewed, viewer.Email, "Customer",
            Target: contentHash, ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(), Payload: auditPayload), ct);

        // Never serve anything scriptable inline — the shared guard
        // (AttachmentResponse, audit v0.1.1 #2) forces a download for
        // HTML/SVG/XML/JS regardless of what the sender declared.
        return Servicedesk.Api.Tickets.AttachmentResponse.File(stream, mimeType, originalFilename, inline);
    }
}
