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
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

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
            [FromServices] Npgsql.NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

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
                detail.Ticket, first.CreatedEventMetadataJson, dataSource, ct);

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
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
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

        group.MapPost("/", async (
            [FromBody] CreateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IContactLookupService contactLookup,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla,
            ITriggerService triggers, ISettingsService settings,
            Servicedesk.Infrastructure.Tickets.ITicketHtmlSanitizer htmlSanitizer, TimeProvider clock, CancellationToken ct) =>
        {
            req = req with
            {
                BodyHtml = htmlSanitizer.Sanitize(req.BodyHtml),
                InitialNote = req.InitialNote is { } initialNote
                    ? initialNote with { BodyHtml = htmlSanitizer.Sanitize(initialNote.BodyHtml) ?? string.Empty }
                    : null,
            };
            if (string.IsNullOrWhiteSpace(req.Subject))
                return Results.BadRequest(new { error = "Subject is required." });
            if (req.RequesterContactId == Guid.Empty)
                return Results.BadRequest(new { error = "requesterContactId is required." });
            // v0.1.44 — same rule as the side panel: never a past reminder.
            // Checked before any write (company link learning below).
            if (PendingTillRules.IsInPast(req.PendingTillUtc, clock.GetUtcNow().UtcDateTime))
                return Results.BadRequest(new { error = PendingTillRules.PastMessage, code = PendingTillRules.PastCode });

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            // v0.0.105 — project tickets are pinned to the configured project
            // queue: a project create lands there regardless of the queue the
            // client sent (the drawer hides the queue field in that case).
            var effectiveQueueId = req.QueueId;
            if (req.IsProject && await ProjectQueuePin.GetPinnedQueueIdAsync(settings, ct) is Guid pinnedQueueId)
                effectiveQueueId = pinnedQueueId;

            if (!await queueAccess.HasQueueAccessAsync(userId, userRole, effectiveQueueId, ct))
                return Results.Json(new { error = "You do not have access to this queue." }, statusCode: 403);

            var requester = await companies.GetContactAsync(req.RequesterContactId, ct);
            if (requester is null) return Results.BadRequest(new { error = "Unknown requester contact." });

            // Optional parent-link: the "Create linked ticket" flow on a
            // source ticket passes its own id here so the new ticket lands
            // as a sub-ticket. Validate access + non-merged + non-self
            // before insert; the link is applied in a follow-up call once
            // the new ticket exists.
            if (req.ParentTicketId is Guid parentId)
            {
                if (parentId == Guid.Empty)
                    return Results.BadRequest(new { error = "parentTicketId is empty." });
                // A parent in a queue the caller can't see answers exactly
                // like a missing one, so an id probe learns nothing.
                var parent = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, parentId, ct);
                if (parent is null)
                    return Results.BadRequest(new { error = "Parent ticket not found." });
                if (parent.MergedIntoTicketId is not null)
                    return Results.Conflict(new { error = "Parent ticket is merged.", code = "parent_is_merged" });
            }

            // v0.0.51 — when the UI passed an explicit CompanyId the
            // agent already made the decision in the create-time popup,
            // so the cascade is skipped. Mail-intake leaves CompanyId
            // null and falls through to the same ResolveCompanyForNewTicketAsync
            // path it has always used. NewLinkRole carries the learn-
            // flow choice (primary/secondary/supplier) for picks that
            // aren't yet on the contact's link list.
            Guid? resolvedCompanyId;
            bool awaitingCompany;
            string? resolvedVia;
            if (req.CompanyId is Guid pickedCompanyId)
            {
                var linkResult = await ApplyTicketCompanyChoiceAsync(
                    companies, req.RequesterContactId, pickedCompanyId, req.NewLinkRole, ct);
                if (linkResult.ErrorResult is not null) return linkResult.ErrorResult;

                resolvedCompanyId = pickedCompanyId;
                awaitingCompany = false;
                resolvedVia = "manual";
            }
            else
            {
                var resolution = await contactLookup.ResolveCompanyForNewTicketAsync(req.RequesterContactId, ct);
                resolvedCompanyId = resolution.CompanyId;
                awaitingCompany = resolution.Awaiting;
                resolvedVia = resolution.ResolvedVia;
            }

            var pendingTillUtc = req.PendingTillUtc;

            var created = await tickets.CreateAsync(new NewTicket(
                Subject: req.Subject.Trim(),
                BodyText: req.BodyText ?? "",
                BodyHtml: req.BodyHtml,
                RequesterContactId: req.RequesterContactId,
                QueueId: effectiveQueueId,
                StatusId: req.StatusId,
                PriorityId: req.PriorityId,
                CategoryId: req.CategoryId,
                AssigneeUserId: req.AssigneeUserId,
                Source: req.Source ?? "Api",
                CompanyId: resolvedCompanyId,
                AwaitingCompanyAssignment: awaitingCompany,
                CompanyResolvedVia: resolvedVia,
                PendingTillUtc: pendingTillUtc,
                TicketTypeId: req.TicketTypeId,
                InitialNote: req.InitialNote is { } note && !string.IsNullOrWhiteSpace(note.BodyHtml)
                    ? new InitialTicketNote(note.BodyHtml, note.IsInternal)
                    : null,
                IsProject: req.IsProject,
                IsCallback: req.IsCallback), ct);

            // Apply the parent link after create so the new ticket exists
            // for the FK + cycle-check. Failure here is logged but does not
            // roll the new ticket back — the agent can re-link from the
            // side panel afterwards if validation rejected.
            if (req.ParentTicketId is Guid parentTicketId)
            {
                var linkResult = await tickets.LinkParentAsync(created.Id, parentTicketId, userId, ct);
                if (!linkResult.Success)
                {
                    return Results.Conflict(new
                    {
                        error = $"Ticket was created but linking to parent failed: {linkResult.FailureReason}",
                        code = "parent_link_failed",
                        ticketId = created.Id,
                    });
                }
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.created",
                Actor: actor,
                ActorRole: role,
                Target: created.Id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { created.Number, created.Subject }));

            // Notify ticket list viewers that a new ticket was created
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", created.Id.ToString(), ct);

            await sla.OnTicketCreatedAsync(created.Id, ct);

            // Trigger evaluator (v0.0.24 Blok 2). Runs after SLA so an
            // action handler that consults SLA-derived fields sees the
            // current deadlines. AllFieldsNew + ArticleAdded=true is the
            // ChangeSet for a fresh ticket — every field is "changed" and
            // a description-event was just written.
            using var origin = Servicedesk.Infrastructure.Triggers.TriggerOrigin.Agent(userId);
            await triggers.EvaluateAsync(
                ticketId: created.Id,
                ticketEventId: null,
                activatorKind: TriggerActivatorKind.Action,
                changeSet: TriggerChangeSet.AllFieldsNew(),
                ct: ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, created.CompanyId, created.RequesterContactId, ct);
            var showAlertOnCreate = companyAlert is not null && companyAlert.AlertOnCreate
                && !string.IsNullOrWhiteSpace(companyAlert.AlertText);
            return Results.Created($"/api/tickets/{created.Id}", new
            {
                ticket = created,
                companyAlert,
                showAlertOnCreate,
            });
        }).WithName("CreateTicket");

        // v0.0.102 — bulk action: post a note/comment and/or change status,
        // queue, assignee, priority on many tickets in one request. Each
        // ticket runs the full single-ticket rule set through the shared
        // mutation service (queue access, status-in-queue, status gates,
        // triggers, SLA, audit, realtime); tickets that fail a rule are
        // skipped and reported, the batch never stops on one ticket. The
        // "bulk" rate-limit policy bounds abuse; the max-selection setting
        // bounds one request's work.
        group.MapPost("/bulk", async (
            [FromBody] BulkTicketActionRequest req, HttpContext http,
            ITicketBulkActionService bulk, CancellationToken ct) =>
        {
            if (req.TicketIds is null || req.TicketIds.Count == 0)
                return Results.BadRequest(new { error = "ticketIds is required.", code = "no_tickets" });
            if (req.TicketIds.Count > TicketBulkActionService.HardMaxSelection)
                return Results.BadRequest(new
                {
                    error = $"A bulk action may touch at most {TicketBulkActionService.HardMaxSelection} tickets.",
                    code = "too_many",
                });
            if (req.MessageHtml is { Length: > 200_000 })
                return Results.BadRequest(new { error = "Message is too long.", code = "message_too_long" });

            var actor = ResolveMutationActor(http);
            try
            {
                var result = await bulk.ExecuteAsync(actor, new TicketBulkActionRequest(
                    TicketIds: req.TicketIds,
                    MessageHtml: string.IsNullOrWhiteSpace(req.MessageHtml) ? null : req.MessageHtml,
                    MessageIsInternal: req.MessageIsInternal ?? true,
                    StatusId: req.StatusId,
                    QueueId: req.QueueId,
                    PriorityId: req.PriorityId,
                    AssigneeUserId: req.AssigneeUserId,
                    UnassignAssignee: req.UnassignAssignee,
                    PendingTillUtc: req.PendingTillUtc), ct);
                return Results.Ok(result);
            }
            catch (TicketBulkActionRejectedException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.Code });
            }
        }).WithName("BulkTicketAction").RequireRateLimiting("bulk");

        group.MapPatch("/{id:guid}", async (
            Guid id, [FromBody] UpdateTicketRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies,
            ITicketMutationService mutations, IStatusGateConfirmationService gateConfirmations,
            Servicedesk.Infrastructure.Persistence.Taxonomy.ITaxonomyRepository taxonomy, CancellationToken ct) =>
        {
            var userId = ActorContext.GetUserId(http);
            if (req.ClearCategory && req.CategoryId.HasValue)
                return Results.BadRequest(new { error = "Choose a category or clear it, not both.", code = "category_conflict" });
            if (req.CategoryId is Guid categoryId)
            {
                var category = await taxonomy.GetCategoryAsync(categoryId, ct);
                if (category is null || !category.IsActive)
                    return Results.BadRequest(new { error = "Unknown or inactive category.", code = "category_invalid" });
            }
            var actor = ResolveMutationActor(http);

            // v0.0.102 — the rule set (queue access on current + target
            // queue, status-in-queue-scope, status-gate probe) lives in the
            // shared mutation service so the bulk action runs the very same
            // checks per ticket. The endpoint only maps verdicts to HTTP.
            var requested = new TicketFieldUpdate(
                QueueId: req.QueueId,
                StatusId: req.StatusId,
                PriorityId: req.PriorityId,
                CategoryId: req.CategoryId,
                AssigneeUserId: req.AssigneeUserId,
                ClearAssignee: req.UnassignAssignee,
                Subject: req.Subject?.Trim(),
                BodyText: req.BodyText,
                BodyHtml: req.BodyHtml,
                PendingTillUtc: req.PendingTillUtc,
                IsCallback: req.IsCallback,
                IsResearch: req.IsResearch,
                DescriptionInternal: req.DescriptionInternal,
                ClearCategory: req.ClearCategory);
            var pre = await mutations.PrecheckFieldUpdateAsync(actor, id, requested, ct);
            switch (pre.Check)
            {
                case TicketMutationCheck.NotFound:
                case TicketMutationCheck.NoAccess:
                    return Results.NotFound();
                case TicketMutationCheck.TargetQueueNoAccess:
                    return Results.Json(new { error = "You do not have access to the target queue." }, statusCode: 403);
                case TicketMutationCheck.StatusNotInQueueScope:
                    // v0.0.40 polish — catches the rare case where a stale
                    // client form posts a status the dropdown filter would
                    // have hidden.
                    return Results.BadRequest(new
                    {
                        error = "This status is not allowed for the target queue.",
                        code = "status_not_in_queue_scope",
                    });
                case TicketMutationCheck.ProjectQueueLocked:
                    // v0.0.105 — project tickets are pinned to the project
                    // queue; the queue selector is hidden client-side, so
                    // this only catches stale clients.
                    return Results.Conflict(new
                    {
                        error = "Project tickets live in the project queue and cannot be moved to another queue.",
                        code = "project_queue_locked",
                    });
                case TicketMutationCheck.PendingTillInPast:
                    // v0.1.44 — the picker disables past moments, so this
                    // only catches a stale tab or a hand-rolled call.
                    return Results.BadRequest(new { error = PendingTillRules.PastMessage, code = PendingTillRules.PastCode });
                case TicketMutationCheck.ChecklistIncomplete:
                    // v0.0.103 — hard block: no confirmation payload can
                    // satisfy it. The body names the checklists so the FE
                    // can offer "Open checklist".
                    return Results.Json(new
                    {
                        error = "Finish the checklist before resolving or closing this ticket.",
                        code = "checklist_incomplete",
                        checklists = (pre.ChecklistBlockers ?? Array.Empty<Servicedesk.Infrastructure.Checklists.ChecklistBlocker>())
                            .Select(b => new { checklistId = b.ChecklistId, name = b.Name, openRequired = b.OpenRequired }),
                    }, statusCode: 409);
            }
            var current = pre.Ticket!;

            // v0.0.42 — status-change gates: only for a real status change on
            // the agent path (triggers, mail-ingest and bulk never negotiate).
            // Missing or incomplete confirmations yield 409 + the unsatisfied
            // gates so the FE can re-open the dialog without re-probing.
            var negotiation = new GateNegotiation(Array.Empty<MatchedStatusGate>(), Array.Empty<ConfirmedStatusGate>());
            if (req.StatusId.HasValue && req.StatusId.Value != current.StatusId && pre.Gates.Count > 0)
            {
                var confirmations = (req.GateConfirmations ?? Array.Empty<GateConfirmationInput>())
                    .Select(c => new GateConfirmation(c.TriggerId, c.Answers, c.CompanyId, c.Role))
                    .ToList();
                negotiation = await gateConfirmations.NegotiateAsync(pre.Gates, confirmations, ct);
                if (negotiation.InvalidCompany)
                    return Results.BadRequest(new { error = "Selected company is not available.", code = "invalid_company" });
                if (negotiation.Missing.Count > 0)
                {
                    return Results.Json(new
                    {
                        error = "Status change requires confirmation from one or more gates.",
                        code = "status_gate_required",
                        gates = negotiation.Missing.Select(g => ProjectGateForApi(g)),
                    }, statusCode: 409);
                }
                // A company-link gate's link is its precondition: applied in
                // the same request, before the status flip.
                await gateConfirmations.ApplyPendingLinksAsync(negotiation, ct);
            }

            // v0.0.89 — a keep_open choice holds the ticket in its current
            // status: the status change is dropped, other edits still apply.
            var update = negotiation.KeepOpen ? requested with { StatusId = null } : requested;
            var detail = await mutations.ApplyFieldUpdateAsync(id, update, userId, ct);
            if (detail is null) return Results.NotFound();

            // Confirmation notes + decision events after the write (they sort
            // after the StatusChange); refresh so the response carries them.
            if (await gateConfirmations.RecordAsync(id, userId, negotiation, ct))
                detail = await tickets.GetByIdAsync(id, ct) ?? detail;

            // Audit → realtime → SLA → triggers, shared with the bulk path.
            // The trigger ChangeSet is built from the raw request (any
            // non-null field counts as "changed", incl. a status the
            // keep-open gate just dropped) — same as before the extraction.
            await mutations.PublishFieldUpdateAsync(
                actor, id, update,
                auditPayload: req,
                changeSet: new TriggerChangeSet(
                    TicketMutationService.DeriveChangedFields(requested), ArticleAdded: false),
                ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.CompanyId, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("UpdateTicket");

        // Manual company assignment (v0.0.9 ToDo #4). Triggered when a ticket
        // was created with awaiting_company_assignment=true because the contact
        // had no primary link, multiple secondaries, or supplier-only links.
        // The reason is computed server-side from the contact's current links
        // so the audit payload can't be spoofed from the client.
        group.MapPatch("/{id:guid}/company", async (
            Guid id, [FromBody] AssignTicketCompanyRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.CompanyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId is required." });

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (current is null) return Results.NotFound();

            // v0.0.51 — validate the company + handle the learn-flow link
            // role choice with the same rules the create-time POST uses.
            // Failure responses (400/409) come back before we touch the
            // ticket so a rejected role can't leave an orphan link behind.
            var linkChoice = await ApplyTicketCompanyChoiceAsync(
                companies, current.RequesterContactId, req.CompanyId, req.NewLinkRole, ct);
            if (linkChoice.ErrorResult is not null) return linkChoice.ErrorResult;

            var targetCompany = await companies.GetCompanyAsync(req.CompanyId, ct);
            // Helper already validated existence + active — re-fetch only
            // to surface the name in the audit payload below.
            if (targetCompany is null) return Results.BadRequest(new { error = "Unknown company." });

            // Infer the resolution reason from the contact's current link set.
            // supplier_only → contact has links but none are primary/secondary.
            // ambiguous_secondary → everything else that landed in awaiting
            // (multiple secondaries, mixed, etc.). Agents overriding an already
            // resolved ticket get reason='override'.
            string reason;
            if (current.AwaitingCompanyAssignment)
            {
                var links = await companies.ListContactLinksAsync(current.RequesterContactId, ct);
                reason = links.All(l => l.Role == "supplier") && links.Count > 0
                    ? "supplier_only"
                    : "ambiguous_secondary";
            }
            else
            {
                reason = "override";
            }

            var detail = await tickets.AssignCompanyAsync(id, req.CompanyId, userId, ct);
            if (detail is null) return Results.NotFound();

            var (actor, actorRole) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.company.manually_assigned",
                Actor: actor,
                ActorRole: actorRole,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    companyId = req.CompanyId,
                    companyName = targetCompany.Name,
                    previousCompanyId = current.CompanyId,
                    reason,
                    newLinkRole = linkChoice.NewLinkCreated ? req.NewLinkRole : null,
                    contactId = current.RequesterContactId,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.CompanyId, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("AssignTicketCompany");

        // v0.0.12: switch the requester on an ongoing ticket. The new contact's
        // company is re-resolved with the same decision tree used at ticket
        // creation (primary → single secondary → supplier_only → none), so the
        // ticket's frozen company_id follows the new requester. Writes a
        // RequesterChange timeline event with from/to contact + company.
        group.MapPatch("/{id:guid}/requester", async (
            Guid id, [FromBody] ChangeTicketRequesterRequest req, HttpContext http,
            ITicketRepository tickets, ICompanyRepository companies, IQueueAccessService queueAccess,
            IContactLookupService contactLookup,
            IHubContext<TicketPresenceHub> hub, IAuditLogger audit, ISlaEngine sla, CancellationToken ct) =>
        {
            if (req.ContactId == Guid.Empty)
                return Results.BadRequest(new { error = "contactId is required." });

            var userId = ActorContext.GetUserId(http);
            var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var current = await tickets.GetAccessibleCoreAsync(queueAccess, userId, userRole, id, ct);
            if (current is null) return Results.NotFound();

            var contact = await companies.GetContactAsync(req.ContactId, ct);
            if (contact is null)
                return Results.BadRequest(new { error = "Unknown contact." });
            if (!contact.IsActive)
                return Results.BadRequest(new { error = "Cannot assign an inactive contact." });

            var resolution = await contactLookup.ResolveCompanyForNewTicketAsync(req.ContactId, ct);

            var detail = await tickets.ChangeRequesterAsync(
                ticketId: id,
                newContactId: req.ContactId,
                newCompanyId: resolution.CompanyId,
                awaitingCompanyAssignment: resolution.Awaiting,
                companyResolvedVia: resolution.ResolvedVia,
                actorUserId: userId,
                ct: ct);
            if (detail is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket.requester.changed",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new
                {
                    fromContactId = current.RequesterContactId,
                    toContactId = req.ContactId,
                    fromCompanyId = current.CompanyId,
                    toCompanyId = resolution.CompanyId,
                    resolvedVia = resolution.ResolvedVia,
                    awaiting = resolution.Awaiting,
                }));

            var ticketIdStr = id.ToString();
            await hub.Clients.Group($"ticket:{ticketIdStr}").SendAsync("TicketUpdated", ticketIdStr, ct);
            await hub.Clients.Group("ticket-list").SendAsync("TicketListUpdated", ticketIdStr, ct);

            await sla.OnTicketFieldsChangedAsync(id, ct);

            var companyAlert = await BuildCompanyAlertAsync(companies, detail.Ticket.CompanyId, detail.Ticket.RequesterContactId, ct);
            return Results.Ok(new
            {
                ticket = detail.Ticket,
                body = detail.Body,
                pinnedEvents = detail.PinnedEvents,
                companyAlert,
            });
        }).WithName("ChangeTicketRequester");

        return app;
    }

    // v0.0.40 polish — parse comma-separated guid query strings sent by
    // the view editor's multi-select. Invalid tokens are silently dropped.
    // Returns null for empty input so the repository treats it as "no
    // multi-filter" and falls back to the singular field path.
    private static IReadOnlyList<Guid>? ParseGuidList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var result = new List<Guid>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(token, out var g)) result.Add(g);
        }
        return result.Count == 0 ? null : result;
    }

    /// v0.1.18 — a normalized search term that is a clean positive integer is
    /// also probed against the ticket number (typed bigint, indexed column).
    internal static long? ParseTicketNumberProbe(string? normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var span = normalized.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];
        if (span.Length is 0 or > 18) return null;
        foreach (var c in span)
            if (c is < '0' or > '9') return null;
        return long.TryParse(span, global::System.Globalization.NumberStyles.None,
            global::System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// v0.0.57 — normalize a user-typed ticket search so a pasted reference
    /// ("Ticket#1234", "#1234", "[Ticket#1234]") resolves to the bare number
    /// the list/picker SQL matches on. Free text and bare numbers pass through
    /// unchanged. A settings-store hiccup falls back to the factory prefix so
    /// search never 500s.
    private static async Task<string?> NormalizeTicketSearchAsync(
        string? search, ISettingsService settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search)) return search;
        var raw = await settings.GetOrAsync<string?>(SettingKeys.Tickets.ReferencePrefix, null, ct);
        var prefix = string.IsNullOrWhiteSpace(raw) ? TicketReference.DefaultPrefix : raw;
        return TicketReference.NormalizeSearchTerm(search, prefix);
    }

    /// Shared projection for both the probe endpoint and the PATCH 409
    /// payload. Keeps the shape identical so the FE renders an
    /// out-of-band match (409 fallback) the same as a probe-then-PATCH
    /// match. The TriggerId, kind, dialog labels and (when applicable)
    /// the requester contact info are the only fields the dialog
    /// needs — note_template stays server-side.
    private static object ProjectGateForApi(MatchedStatusGate g) => new
    {
        triggerId = g.TriggerId,
        name = g.Name,
        kind = g.Kind,
        title = g.Title,
        message = g.Message,
        confirmLabel = g.ConfirmLabel,
        cancelLabel = g.CancelLabel,
        questions = g.Questions.Select(q => new
        {
            key = q.Key,
            type = q.Type,
            label = q.Label,
            required = q.Required,
            yesLabel = q.YesLabel,
            noLabel = q.NoLabel,
            // v0.0.89 — only populated for choice questions; the outcome is
            // surfaced so the dialog can hint what each option will do.
            options = (q.Options ?? Array.Empty<GateChoiceOption>())
                .Select(o => new { label = o.Label, outcome = o.Outcome }),
        }),
        requesterContactId = g.RequesterContactId,
        requesterDisplayName = g.RequesterDisplayName,
    };

    private static object ProjectFirstOpenGateForApi(MatchedFirstOpenGate g, Guid ticketId) => new
    {
        triggerId = g.TriggerId,
        name = g.Name,
        title = g.Title,
        message = g.Message,
        fieldLabel = g.FieldLabel,
        confirmLabel = g.ConfirmLabel,
        currentSubject = g.CurrentSubject,
        showRequest = g.ShowRequest,
        requestBodyHtml = g.RequestBodyHtml,
        requestBodyText = g.RequestBodyText,
        requestSource = g.RequestFromMail ? "mail" : "ticket",
        // Reuses the existing audited raw-mail endpoint (Agent + queue-access
        // gated); null when the mail's .eml blob was never stored.
        requestEmlUrl = g.RequestMailId is Guid mailId
            ? $"/api/tickets/{ticketId}/mail/{mailId}/raw"
            : null,
    };

    /// v0.0.102 — one actor object for the shared mutation service: the
    /// authorization identity (user id + role claim) plus the audit identity
    /// the endpoints already derived via <see cref="ActorContext.Resolve"/>.
    internal static TicketMutationActor ResolveMutationActor(HttpContext http)
    {
        var userId = ActorContext.GetUserId(http);
        var userRole = http.User.FindFirst(ClaimTypes.Role)!.Value;
        var (actor, role) = ActorContext.Resolve(http);
        return new TicketMutationActor(
            userId, userRole, actor, role,
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent.ToString())
        {
            Amr = http.User.FindFirst(Servicedesk.Api.Auth.SessionAuthenticationHandler.AmrClaimType)?.Value,
        };
    }

    private const int MaxEditAttachmentIds = 20;

    private static IReadOnlyList<GraphRecipient> MapRecipients(IReadOnlyList<MailRecipientInput>? input)
    {
        if (input is null || input.Count == 0) return Array.Empty<GraphRecipient>();
        var list = new List<GraphRecipient>(input.Count);
        foreach (var r in input)
        {
            if (string.IsNullOrWhiteSpace(r.Address)) continue;
            list.Add(new GraphRecipient(r.Address.Trim(), (r.Name ?? r.Address).Trim()));
        }
        return list;
    }

    /// Hide survey-result events from the timeline for non-admin agents.
    /// SurveySent stays visible as operational context ("we asked the
    /// customer for feedback"), but SurveySubmitted and SurveyExpired carry
    /// the response-cycle outcome — that's admin-only per the v0.0.38
    /// post-test access tightening. EventType is stored on the record as a
    /// string (matches the DB CHECK constraint), so we compare names rather
    /// than enum values.
    /// v0.1.33 — event types a role never sees on the timeline (survey events
    /// are Admin-only), applied in SQL by the paged reader so page sizes and
    /// counts match.
    private static readonly string[] AdminOnlyTimelineEventTypes =
    {
        nameof(Servicedesk.Domain.Tickets.TicketEventType.SurveySubmitted),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.SurveyExpired),
    };

    /// The event types a personal or group tag can point to (the
    /// MentionNotificationService sources) — the only ones the tag hover
    /// preview serves.
    private static readonly HashSet<string> TagPreviewEventTypes = new(StringComparer.Ordinal)
    {
        nameof(Servicedesk.Domain.Tickets.TicketEventType.Note),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.Comment),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.Call),
        nameof(Servicedesk.Domain.Tickets.TicketEventType.MailSent),
    };

    private static IReadOnlyCollection<string> HiddenTimelineEventTypes(string role) =>
        string.Equals(role, "Admin", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : AdminOnlyTimelineEventTypes;

    /// Tickets.TimelinePageSize: 0 = whole timeline, else clamped to 10–1000.
    private static async Task<int> ResolveTimelinePageSizeAsync(ISettingsService settings, CancellationToken ct)
    {
        int raw;
        raw = await settings.GetOrAsync<int>(SettingKeys.Tickets.TimelinePageSize, 50, ct);
        return raw <= 0 ? 0 : Math.Clamp(raw, 10, 1000);
    }

    // Hard ceiling for one "load older" request, also when it loads back to a
    // linked event or loads everything — keeps a single response bounded.
    private const int MaxTimelineSliceEvents = 2000;

    /// v0.0.51 — shared validation + side-effect for the two endpoints
    /// that let the agent attach a ticket to a specific company
    /// (POST /api/tickets and PATCH /api/tickets/{id}/company).
    ///
    /// Rules:
    ///   * Company must exist and be active.
    ///   * If the company is already on the contact's link list →
    ///     newLinkRole must be null/empty (anything else = 400).
    ///   * If the company is NOT yet linked → newLinkRole must be
    ///     'primary' | 'secondary' | 'supplier' (anything else = 400).
    ///   * newLinkRole='primary' while the contact already has a
    ///     primary link to another company → 409 (no silent demotion;
    ///     the agent must change the existing primary on the Contact
    ///     page first).
    ///
    /// Returns a non-null <c>ErrorResult</c> when validation rejects
    /// the request — caller must return it before persisting the
    /// ticket / company-assignment. <c>NewLinkCreated</c> is true when
    /// a fresh contact_companies row was upserted.
    private static async Task<TicketCompanyChoiceResult> ApplyTicketCompanyChoiceAsync(
        ICompanyRepository companies,
        Guid contactId,
        Guid pickedCompanyId,
        string? newLinkRole,
        CancellationToken ct)
    {
        if (pickedCompanyId == Guid.Empty)
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new { error = "companyId is empty." }));

        var picked = await companies.GetCompanyAsync(pickedCompanyId, ct);
        if (picked is null)
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new { error = "Unknown company." }));
        if (!picked.IsActive)
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new { error = "Cannot assign an inactive company." }));

        var trimmedRole = string.IsNullOrWhiteSpace(newLinkRole) ? null : newLinkRole.Trim().ToLowerInvariant();
        var links = await companies.ListContactLinksAsync(contactId, ct);
        var alreadyLinked = links.Any(l => l.CompanyId == pickedCompanyId);

        if (alreadyLinked)
        {
            if (trimmedRole is not null)
            {
                return TicketCompanyChoiceResult.Error(Results.BadRequest(new
                {
                    error = "newLinkRole must be omitted when the company is already linked to the contact.",
                    code = "company_already_linked",
                }));
            }
            return TicketCompanyChoiceResult.Ok(NewLinkCreated: false);
        }

        // Company is not yet linked → a role choice is mandatory.
        if (trimmedRole is null)
        {
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new
            {
                error = "newLinkRole is required when picking a company that is not yet linked to the contact.",
                code = "new_link_role_required",
            }));
        }
        if (trimmedRole is not ("primary" or "secondary" or "supplier"))
        {
            return TicketCompanyChoiceResult.Error(Results.BadRequest(new
            {
                error = "newLinkRole must be one of 'primary', 'secondary', 'supplier'.",
                code = "new_link_role_invalid",
            }));
        }

        // Primary slot is unique per contact. Refuse rather than
        // silently demote so the agent always knows what changes.
        if (trimmedRole == "primary")
        {
            var existingPrimary = links.FirstOrDefault(l => l.Role == "primary");
            if (existingPrimary is not null)
            {
                return TicketCompanyChoiceResult.Error(Results.Conflict(new
                {
                    error = "This contact already has a primary company. Pick a different role, or change the primary on the contact page first.",
                    code = "primary_already_set",
                    existingPrimaryCompanyId = existingPrimary.CompanyId,
                }));
            }
        }

        await companies.UpsertContactLinkAsync(contactId, pickedCompanyId, trimmedRole, ct);
        return TicketCompanyChoiceResult.Ok(NewLinkCreated: true);
    }

    private readonly record struct TicketCompanyChoiceResult(IResult? ErrorResult, bool NewLinkCreated)
    {
        public static TicketCompanyChoiceResult Ok(bool NewLinkCreated) => new(null, NewLinkCreated);
        public static TicketCompanyChoiceResult Error(IResult result) => new(result, false);
    }

    private static async Task<TicketCompanyAlert?> BuildCompanyAlertAsync(
        ICompanyRepository companies, Guid? ticketCompanyId, Guid requesterContactId, CancellationToken ct)
    {
        // Resolve the alert from the ticket's OWN frozen company first — that is
        // the company this ticket belongs to, set at intake (t.company_id). The
        // requester's *current* primary-company link is only a fallback for
        // tickets that never got a company assigned. Resolving via the requester
        // link alone meant older tickets — whose requester contact has no primary
        // contact_companies row even though the ticket has a company — never
        // surfaced the on-open pop-up. (v0.0.66 fix)
        var company = ticketCompanyId is { } cid
            ? await companies.GetCompanyAsync(cid, ct)
            : null;
        company ??= await companies.GetPrimaryCompanyForContactAsync(requesterContactId, ct);
        if (company is null) return null;
        return new TicketCompanyAlert(
            CompanyId: company.Id,
            CompanyName: company.Name,
            Code: company.Code,
            AlertText: company.AlertText,
            AlertOnCreate: company.AlertOnCreate,
            AlertOnOpen: company.AlertOnOpen,
            AlertOnOpenMode: company.AlertOnOpenMode);
    }

    /// v0.0.23 split: when this ticket was created by splitting a mail off
    /// another ticket, surface the source-mail's non-inline attachments so the
    /// description block can render download chips. URLs route through the
    /// source ticket's per-mail attachment endpoint — the bytes never move.
    /// Returns an empty list for tickets that aren't splits.
    private static async Task<IReadOnlyList<object>> BuildSplitDescriptionAttachmentsAsync(
        Servicedesk.Domain.Tickets.Ticket ticket, string? createdEventMetadataJson,
        Npgsql.NpgsqlDataSource dataSource, CancellationToken ct)
    {
        if (ticket.SplitFromTicketId is null) return Array.Empty<object>();

        // Created event carries splitFromMailMessageId in its metadata (set by
        // SplitAsync). v0.1.33 — read straight from that event (the timeline
        // is paged, so it is usually not in the loaded events).
        if (string.IsNullOrWhiteSpace(createdEventMetadataJson))
            return Array.Empty<object>();

        Guid? sourceMailMessageId = null;
        try
        {
            using var doc = JsonDocument.Parse(createdEventMetadataJson);
            if (doc.RootElement.TryGetProperty("splitFromMailMessageId", out var prop)
                && prop.ValueKind == JsonValueKind.String
                && Guid.TryParse(prop.GetString(), out var g))
            {
                sourceMailMessageId = g;
            }
        }
        catch { /* malformed metadata — treat as no attachments */ }

        if (sourceMailMessageId is null) return Array.Empty<object>();

        const string sql = """
            SELECT a.id, a.original_filename, a.mime_type, a.size_bytes
            FROM attachments a
            WHERE a.owner_kind = 'Mail'
              AND a.owner_id = @mailId
              AND a.processing_state = 'Ready'
              AND a.is_inline = FALSE
            ORDER BY a.created_utc, a.id
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await Dapper.SqlMapper.QueryAsync<(Guid Id, string OriginalFilename, string MimeType, long SizeBytes)>(
            conn, new Dapper.CommandDefinition(sql, new { mailId = sourceMailMessageId.Value }, cancellationToken: ct));

        var sourceTicketId = ticket.SplitFromTicketId.Value;
        var mailId = sourceMailMessageId.Value;
        return rows.Select(r => (object)new
        {
            id = r.Id,
            name = r.OriginalFilename,
            mimeType = r.MimeType,
            size = r.SizeBytes,
            url = $"/api/tickets/{sourceTicketId}/mail/{mailId}/attachments/{r.Id}",
        }).ToList();
    }
}
