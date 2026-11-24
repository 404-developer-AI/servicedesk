using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Signatures;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Surveys;
using Servicedesk.Infrastructure.TaggingMailboxes;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Servicedesk.Infrastructure.Triggers.FirstOpenGate;
using Servicedesk.Infrastructure.Workflow;

namespace Servicedesk.Api.Tickets;

/// v0.0.5 ticket API surface: list/get/create backed by the Dapper
/// repository. The list page uses keyset pagination — no offset, so it
/// stays fast as the dataset grows. Tickets are Agent+Admin only until the
/// customer portal lands. The dev-only benchmark + seed endpoints sit
/// behind an <see cref="IWebHostEnvironment.IsDevelopment"/> gate so they
/// cannot ship to production.
public static partial class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", async (
            Guid? queueId, Guid? statusId, Guid? priorityId, Guid? assigneeUserId,
            Guid? requesterContactId, Guid? companyId, string? search, bool? openOnly, bool? openFirst,
            bool? stateBucketSort, bool? projectsOnly,
            string? sortField, string? sortDirection, bool? priorityFloat, int? offset,
            DateTime? cursorUpdatedUtc, Guid? cursorId, int? limit,
            // v0.0.40 polish — multi-select filters from saved views. Comma-
            // separated Guids; invalid tokens are silently dropped so a
            // partially valid query still returns a sensible page.
            string? queueIds, string? statusIds, string? priorityIds,
            // v0.1.17 — Call-back / Research floats + "only" view filters.
            bool? callbackFloat, bool? researchFloat, bool? callbacksOnly, bool? researchOnly,
            // v0.1.18 — per-view search box (q + qMode full|columns; qFields =
            // visible column ids for the columns-mode fallback) and the
            // opt-in "Time logged" column.
            string? q, string? qMode, string? qFields, bool? includeTimeLogged,
            HttpContext http, ITicketRepository repo, IQueueAccessService queueAccess,
            ISettingsService settings, CancellationToken ct) =>
        {
            string? fullTsQuery = null;
            long? fullNumber = null;
            string? columnSearch = null;
            IReadOnlyList<string>? columnFields = null;
            if (!string.IsNullOrWhiteSpace(q))
            {
                if (q.Length > TicketColumnSearch.MaxTermLength)
                    return Results.BadRequest(new { error = $"Search is limited to {TicketColumnSearch.MaxTermLength} characters." });
                if (string.Equals(qMode, "columns", StringComparison.Ordinal))
                {
                    columnFields = TicketColumnSearch.FilterFields(qFields?.Split(','));
                    if (columnFields.Count == 0)
                        return Results.BadRequest(new { error = "No searchable columns given." });
                    columnSearch = q.Trim();
                }
                else if (qMode is null || string.Equals(qMode, "full", StringComparison.Ordinal))
                {
                    var normalizedQ = await NormalizeTicketSearchAsync(q, settings, ct);
                    fullTsQuery = Servicedesk.Infrastructure.Search.TicketTsQuery.BuildPrefix(normalizedQ);
                    fullNumber = ParseTicketNumberProbe(normalizedQ);
                }
                else
                {
                    return Results.BadRequest(new { error = "qMode must be 'full' or 'columns'." });
                }
            }

            var userId = ActorContext.GetUserId(http);
            var role = ActorContext.GetRole(http.User);

            // Queue-access enforcement: admins get null (no filter), agents
            // get their assigned queue list. An agent with zero queues gets
            // an empty page immediately.
            IReadOnlyList<Guid>? accessibleQueueIds = null;
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                accessibleQueueIds = await queueAccess.GetAccessibleQueueIdsAsync(userId, role, ct);
                if (accessibleQueueIds.Count == 0)
                    return Results.Ok(new { items = Array.Empty<object>(), nextCursor = (object?)null, nextOffset = (int?)null });
            }

            var normalizedSearch = await NormalizeTicketSearchAsync(search, settings, ct);

            // Single-load model (v0.0.57): the list + views load all matching
            // rows in one request up to the admin-configurable cap. No client
            // limit → fall back to the setting. The repo clamps to a hard
            // ceiling regardless.
            int maxRows;
            maxRows = await settings.GetOrAsync<int>(SettingKeys.Tickets.ListPageSize, 1000, ct);
            if (maxRows < 1) maxRows = 1000;

            var query = new TicketQuery(
                QueueId: queueId, StatusId: statusId, PriorityId: priorityId,
                AssigneeUserId: assigneeUserId, RequesterContactId: requesterContactId,
                RequesterCompanyId: companyId,
                Search: normalizedSearch, OpenOnly: openOnly ?? false, OpenFirst: openFirst ?? false,
                StateBucketSort: stateBucketSort ?? false,
                SortField: sortField, SortDirection: sortDirection,
                PriorityFloat: priorityFloat ?? false, Offset: offset,
                CursorUpdatedUtc: cursorUpdatedUtc, CursorId: cursorId,
                Limit: limit ?? maxRows,
                AccessibleQueueIds: accessibleQueueIds,
                QueueIds: ParseGuidList(queueIds),
                StatusIds: ParseGuidList(statusIds),
                PriorityIds: ParseGuidList(priorityIds),
                ProjectsOnly: projectsOnly ?? false,
                CallbackFloat: callbackFloat ?? false,
                ResearchFloat: researchFloat ?? false,
                CallbacksOnly: callbacksOnly ?? false,
                ResearchOnly: researchOnly ?? false,
                FullSearchTsQuery: fullTsQuery,
                FullSearchNumber: fullNumber,
                ColumnSearch: columnSearch,
                ColumnSearchFields: columnFields,
                IncludeTimeLogged: includeTimeLogged ?? false);
            var page = await repo.SearchAsync(query, VisibilityScope.All, null, null, ct);
            return Results.Ok(new
            {
                items = page.Items,
                nextCursor = page.NextCursorUpdatedUtc.HasValue && page.NextCursorId.HasValue
                    ? new { updatedUtc = page.NextCursorUpdatedUtc, id = page.NextCursorId }
                    : null,
                nextOffset = page.NextOffset,
            });
        }).WithName("ListTickets");

        group.MapGet("/{id:guid}", async (
            Guid id, HttpContext http, ITicketRepository repo, ITicketTimelineReader timeline,
            IQueueAccessService queueAccess,
            IMailTimelineEnricher mailEnricher, ISettingsService settings,
            Servicedesk.Infrastructure.Auth.IUserService users,
            Servicedesk.Infrastructure.Mail.Attachments.IAttachmentRepository attachments, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var role = ActorContext.GetRole(http.User);

            // v0.1.33 — the latest page of the timeline only (older events via
            // GET /{id}/events), plus what the page used to compute over all
            // events: counts per type, latest inbound mail, pinned events,
            // tagged orders and the Created event's metadata.
            var pageSize = await ResolveTimelinePageSizeAsync(settings, ct);
            var first = await timeline.GetFirstPageAsync(id, pageSize, HiddenTimelineEventTypes(role), ct);
            if (first is null) return Results.NotFound();
            var detail = first.Detail;

            if (!await queueAccess.HasQueueAccessAsync(userId, role, detail.Ticket.QueueId, ct))
                return Results.NotFound(); // 404 to prevent existence leaking

            // Enrich the page and the out-of-page extras in one batched pass.
            var extras = new List<Servicedesk.Domain.Tickets.TicketEvent>(first.PinnedEventsOutsidePage);
            if (first.LatestMailReceived is { } lm && extras.All(e => e.Id != lm.Id)) extras.Add(lm);
            var enriched = await mailEnricher.EnrichAsync(
                detail with { Events = detail.Events.Concat(extras).ToList() }, ct);
            var pageIds = detail.Events.Select(e => e.Id).ToHashSet();
            var enrichedById = enriched.Events.ToDictionary(e => e.Id);
            detail = enriched with { Events = enriched.Events.Where(e => pageIds.Contains(e.Id)).ToList() };
            var latestMailReceived = first.LatestMailReceived is { } l ? enrichedById.GetValueOrDefault(l.Id) : null;
            var pinnedEventsOutsidePage = first.PinnedEventsOutsidePage
                .Select(e => enrichedById.GetValueOrDefault(e.Id) ?? e).ToList();

            // v0.0.101: merge/split banners, linked parent/children and the
            // company-alert source used to be six sequential lookups, each on
            // its own pooled connection, plus up to four actor/number probes.
            // Now one batched round-trip (see GetDetailRelationsAsync).
            var rel = await repo.GetDetailRelationsAsync(id, ct)
                ?? new TicketDetailRelations(
                    Array.Empty<long>(), null, null, null, null,
                    Array.Empty<SplitChildTicket>(), null, Array.Empty<LinkedChildTicket>(), null);
            var companyAlert = rel.CompanyAlert is { } ca
                ? new TicketCompanyAlert(
                    CompanyId: ca.CompanyId,
                    CompanyName: ca.CompanyName,
                    Code: ca.Code,
                    AlertText: ca.AlertText,
                    AlertOnCreate: ca.AlertOnCreate,
                    AlertOnOpen: ca.AlertOnOpen,
                    AlertOnOpenMode: ca.AlertOnOpenMode)
                : null;

            // For tickets created via split, surface the source-mail's non-inline
            // attachments so the description block can render download chips.
            // The bytes stay on the source mail; the URLs route through the
            // source ticket's mail-attachment endpoint, which the agent can
            // already access (split requires queue access on the source).
            var descriptionAttachments = await BuildSplitDescriptionAttachmentsAsync(
                detail.Ticket, first.CreatedEventMetadataJson, attachments, ct);

            // v0.1.0 — a system ticket opened for a portal registration
            // carries the pending account so the detail page can render the
            // approve/reject card. One indexed point lookup
            // (ix_portal_accounts_ticket); null for every other ticket.
            object? portalRegistration = null;
            if (detail.Ticket.Source == "System")
            {
                var portalAccounts = http.RequestServices.GetService<Servicedesk.Infrastructure.Portal.IPortalAccountRepository>();
                var reg = portalAccounts is null ? null : await portalAccounts.GetByApprovalTicketAsync(id, ct);
                if (reg is not null)
                    portalRegistration = new { userId = reg.UserId, status = reg.Status, email = reg.Email, displayName = reg.DisplayName };
            }

            // v0.1.36 — "::" order pills across the whole timeline for the side
            // panel's Orders block (the page alone may miss older ones). Only
            // the Orders feature shows them, so only its users pay for the
            // body scan; it used to be a separate request per ticket open.
            var taggedOrders = await users.GetAdsolutOrdersEnabledAsync(userId, ct)
                ? (await timeline.ListTaggedOrdersAsync(id, ct)).Select(o => new { id = o.Id, label = o.Label }).ToList()
                : null;

            // v0.1.52 — filled forms for the side panel's Forms block (one
            // indexed read; access is already proven by the ticket load above).
            var formRepo = http.RequestServices.GetRequiredService<Servicedesk.Infrastructure.Forms.ITicketFormRepository>();
            var forms = (await formRepo.ListForTicketAsync(id, ct))
                .Select(Servicedesk.Api.Forms.TicketFormEndpoints.MapSummary).ToList();

            return Results.Ok(new
            {
                ticket = detail.Ticket,
                portalRegistration,
                taggedOrders,
                forms,
                body = detail.Body,
                events = detail.Events,
                hasOlderEvents = first.HasOlderEvents,
                eventTypeCounts = first.EventTypeCounts,
                latestMailReceived,
                pinnedEventsOutsidePage,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
                mergedSourceTicketNumbers = rel.MergedSourceTicketNumbers,
                mergedByUserName = rel.MergedByUserName,
                mergedIntoTicketNumber = rel.MergedIntoTicketNumber,
                splitFromTicketNumber = rel.SplitFromTicketNumber,
                splitFromUserName = rel.SplitFromUserName,
                splitChildren = rel.SplitChildren.Select(c => new { id = c.Id, number = c.Number }),
                descriptionAttachments,
                parentTicketNumber = rel.Parent?.ParentNumber.ToString(),
                parentLinkedByUserName = rel.Parent?.LinkedByName,
                childTickets = rel.ChildTickets.Select(c => new { id = c.Id, number = c.Number.ToString() }),
                projectTicketNumber = rel.Project?.ProjectNumber.ToString(),
                projectTicketSubject = rel.Project?.ProjectSubject,
                projectLinkedByUserName = rel.Project?.LinkedByName,
                projectLinkedTicketCount = rel.ProjectLinkedTicketCount,
                // v0.1.33 — the side panel's "Contact not linked" banner, decided
                // with the detail so it is in place on the first render (it used
                // to appear after two follow-up lookups and push the cards down).
                contactNotLinkedWarning = rel.RequesterCompanyLinkCount == 0
                    && await settings.GetAsync<bool>(SettingKeys.Tickets.ShowContactNotLinkedWarning, ct),
            });
        }).WithName("GetTicket");

        // v0.1.33 — older timeline events, paged backwards from the oldest
        // event the client has. `untilId` loads back to (and including) one
        // specific event — deep links, pinned-event jumps; `all=true` loads
        // the rest of the timeline. Same queue-access rule, role filter and
        // mail enrichment as GET /{id}.
        group.MapGet("/{id:guid}/events", async (
            Guid id, long? beforeId, long? untilId, bool? all, HttpContext http,
            ITicketRepository repo, ITicketTimelineReader timeline, IQueueAccessService queueAccess,
            IMailTimelineEnricher mailEnricher, ISettingsService settings, CancellationToken ct) =>
        {
            if (beforeId is null or <= 0)
                return Results.BadRequest(new { error = "beforeId is required." });
            if (untilId is <= 0)
                return Results.BadRequest(new { error = "untilId must be a positive event id." });

            var userId = ActorContext.GetUserId(http);
            var role = ActorContext.GetRole(http.User);
            var ticket = await repo.GetAccessibleCoreAsync(queueAccess, userId, role, id, ct);
            if (ticket is null) return Results.NotFound();

            var pageSize = await ResolveTimelinePageSizeAsync(settings, ct);
            var limit = all == true || untilId is not null || pageSize == 0
                ? MaxTimelineSliceEvents
                : pageSize;
            var slice = await timeline.ListOlderAsync(
                id, beforeId.Value, untilId, limit, HiddenTimelineEventTypes(role), ct);

            var events = slice.Events;
            if (events.Count > 0)
            {
                var enriched = await mailEnricher.EnrichAsync(new TicketDetail(
                    ticket, new Servicedesk.Domain.Tickets.TicketBody(id, string.Empty, null),
                    events, Array.Empty<Servicedesk.Domain.Tickets.TicketEventPin>()), ct);
                events = enriched.Events;
            }
            return Results.Ok(new { events, hasMore = slice.HasMore });
        }).WithName("ListTicketEvents");

        return app;
    }
}
