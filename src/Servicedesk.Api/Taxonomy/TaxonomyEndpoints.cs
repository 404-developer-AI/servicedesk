using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Api.Taxonomy;

/// Taxonomy CRUD for ticket metadata. Read endpoints (GET) are available
/// to all authenticated agents and admins so dropdowns, filters and the
/// new-ticket drawer work for everyone. Write endpoints (POST/PUT/DELETE)
/// are admin-only. Every write is audit-logged. System rows (seeded
/// defaults) can be renamed/re-colored but never deleted — the repository
/// enforces that invariant so it can't be bypassed here. Rows still
/// referenced by live tickets are also rejected for delete with a 409,
/// so admins can't accidentally orphan data.
public static class TaxonomyEndpoints
{
    public static IEndpointRouteBuilder MapTaxonomyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/taxonomy")
            .WithTags("Taxonomy")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        MapQueues(group);
        MapPriorities(group);
        MapStatuses(group);
        MapCategories(group);
        MapTicketTypes(group);

        return app;
    }

    // ---------- Ticket types (v0.0.39) ----------

    public sealed record TicketTypeRequest(
        [property: Required] string Code,
        [property: Required] string Label,
        string? Description,
        string? Icon,
        string? Color,
        int SortOrder,
        bool IsActive);

    private static void MapTicketTypes(RouteGroupBuilder group)
    {
        // List + GET are agent-visible: the LinkedTicketTypeDialog
        // needs the catalog to render its buttons. Writes stay admin-only.
        group.MapGet("/ticket-types", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListTicketTypesAsync(ct)))
            .WithName("ListTicketTypes").WithOpenApi();

        group.MapGet("/ticket-types/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var t = await repo.GetTicketTypeAsync(id, ct);
            return t is null ? Results.NotFound() : Results.Ok(t);
        }).WithName("GetTicketType").WithOpenApi();

        group.MapPost("/ticket-types", async (
            [FromBody] TicketTypeRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Label) is { } labelErr) return labelErr;
            if (ValidateTicketTypeCode(req.Code) is { } codeErr) return codeErr;
            // Reject duplicate codes early — the unique index would otherwise
            // surface as a 500 from Postgres. CITEXT collation in the column
            // makes the comparison case-insensitive automatically.
            var existing = await repo.GetTicketTypeByCodeAsync(req.Code.Trim(), ct);
            if (existing is not null)
                return Results.Conflict(new { error = $"A ticket type with code '{req.Code}' already exists." });

            var now = DateTime.UtcNow;
            var created = await repo.CreateTicketTypeAsync(new TicketType(
                Id: Guid.Empty,
                Code: req.Code.Trim(),
                Label: req.Label.Trim(),
                Description: req.Description ?? "",
                Icon: Normalize(req.Icon, "ticket"),
                Color: Normalize(req.Color, "#7c7cff"),
                SortOrder: req.SortOrder,
                IsActive: req.IsActive,
                IsSystem: false,
                CreatedUtc: now,
                UpdatedUtc: now), ct);
            await AuditWrite(audit, http, "taxonomy.ticket_type.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/ticket-types/{created.Id}", created);
        }).WithName("CreateTicketType").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/ticket-types/{id:guid}", async (
            Guid id, [FromBody] TicketTypeRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Label) is { } labelErr) return labelErr;
            if (ValidateTicketTypeCode(req.Code) is { } codeErr) return codeErr;
            // Duplicate-code guard ignoring the row being updated.
            var existing = await repo.GetTicketTypeByCodeAsync(req.Code.Trim(), ct);
            if (existing is not null && existing.Id != id)
                return Results.Conflict(new { error = $"A ticket type with code '{req.Code}' already exists." });

            var updated = await repo.UpdateTicketTypeAsync(
                id, req.Code.Trim(), req.Label.Trim(), req.Description ?? "",
                Normalize(req.Icon, "ticket"), Normalize(req.Color, "#7c7cff"),
                req.SortOrder, req.IsActive, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.ticket_type.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdateTicketType").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/ticket-types/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteTicketTypeAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.ticket_type.deleted", id);
        }).WithName("DeleteTicketType").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    private static readonly Regex TicketTypeCodeRegex = new("^[a-z][a-z0-9_-]{1,63}$", RegexOptions.Compiled);

    private static IResult? ValidateTicketTypeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Results.BadRequest(new { error = "Ticket type code is required." });
        if (!TicketTypeCodeRegex.IsMatch(code.Trim()))
            return Results.BadRequest(new { error = "Ticket type code must start with a lowercase letter and contain only lowercase letters, digits, underscores or hyphens (max 64 chars)." });
        return null;
    }

    // ---------- Queues ----------

    public sealed record QueueRequest(
        [property: Required] string Name,
        string? Description,
        string? Color,
        string? Icon,
        int SortOrder,
        bool IsActive,
        string? OutboundMailboxAddress,
        // v0.0.66 — a queue can have many inbound mailbox sources, each its own
        // (mailbox, folder) feeding into this queue. Null/empty = no inbound
        // mail. Each entry with an Id matches an existing source (update);
        // without an Id it's added; existing sources missing from the list are
        // removed.
        InboundMailboxInput[]? InboundMailboxes = null,
        // v0.0.40 polish — per-queue status scope. Null/empty = current
        // behaviour (all statuses available). Non-empty = dropdowns and
        // write-paths only accept these status ids for tickets in this
        // queue. DefaultStatusId drives auto-flip on queue change when
        // the current status is no longer allowed.
        Guid[]? AllowedStatusIds = null,
        Guid? DefaultStatusId = null,
        // Per-queue switch for the Claude AI ticket-assist action. Defaults
        // true so existing queues keep the button when the global feature is on.
        bool AiAssistEnabled = true);

    public sealed record InboundMailboxInput(
        Guid? Id,
        string? Mailbox,
        string? FolderId,
        string? FolderName,
        bool PollingEnabled = true);

    public sealed record QueueInboundMailboxResponse(
        Guid Id,
        string Mailbox,
        string? FolderId,
        string? FolderName,
        bool PollingEnabled,
        DateTime? LastPolledUtc,
        string? LastError,
        int ConsecutiveFailures);

    private static object ToQueueResponse(Queue q, IReadOnlyList<QueueInboundMailbox> sources) => new
    {
        q.Id,
        q.Name,
        q.Slug,
        q.Description,
        q.Color,
        q.Icon,
        q.SortOrder,
        q.IsActive,
        q.IsSystem,
        q.CreatedUtc,
        q.UpdatedUtc,
        // Singular mirror fields are kept for legacy readers + the outbound
        // from-address fallback; the editor uses InboundMailboxes.
        q.InboundMailboxAddress,
        q.OutboundMailboxAddress,
        q.InboundFolderId,
        q.InboundFolderName,
        q.InboundPollingEnabled,
        q.AiAssistEnabled,
        AllowedStatusIds = q.AllowedStatusIds ?? Array.Empty<Guid>(),
        q.DefaultStatusId,
        InboundMailboxes = sources.Select(s => new QueueInboundMailboxResponse(
            s.Id, s.MailboxAddress, s.FolderId, s.FolderName, s.PollingEnabled,
            s.LastPolledUtc, s.LastError, s.ConsecutiveFailures)).ToList(),
    };

    private static void MapQueues(RouteGroupBuilder group)
    {
        group.MapGet("/queues", async (
            ITaxonomyRepository repo, IQueueInboundMailboxRepository sources, CancellationToken ct) =>
        {
            var queues = await repo.ListQueuesAsync(ct);
            var byQueue = (await sources.ListAllAsync(ct))
                .GroupBy(s => s.QueueId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<QueueInboundMailbox>)g.ToList());
            var result = queues
                .Select(q => ToQueueResponse(q,
                    byQueue.TryGetValue(q.Id, out var s) ? s : Array.Empty<QueueInboundMailbox>()))
                .ToList();
            return Results.Ok(result);
        }).WithName("ListQueues").WithOpenApi();

        group.MapGet("/queues/{id:guid}", async (
            Guid id, ITaxonomyRepository repo, IQueueInboundMailboxRepository sources, CancellationToken ct) =>
        {
            var q = await repo.GetQueueAsync(id, ct);
            if (q is null) return Results.NotFound();
            var srcs = await sources.ListByQueueAsync(id, ct);
            return Results.Ok(ToQueueResponse(q, srcs));
        }).WithName("GetQueue").WithOpenApi();

        group.MapPost("/queues", async (
            [FromBody] QueueRequest req, HttpContext http,
            ITaxonomyRepository repo, IQueueInboundMailboxRepository sources,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            if (ValidateMailbox(req.OutboundMailboxAddress, "outboundMailboxAddress") is { } mErr) return mErr;
            var normalized = NormalizeInboundMailboxes(req.InboundMailboxes, out var inErr);
            if (inErr is not null) return inErr;
            // Exclusivity: no other queue may already own any of these sources.
            foreach (var s in normalized)
            {
                var owner = await sources.FindConflictingQueueAsync(s.Mailbox, s.FolderId, null, ct);
                if (owner is not null)
                    return Results.Conflict(new { error = $"The mailbox {s.Mailbox} / {s.FolderName} is already assigned to another queue." });
            }

            var now = DateTime.UtcNow;
            var created = await repo.CreateQueueAsync(new Queue(
                Guid.Empty, req.Name.Trim(), Slugify(req.Name), req.Description ?? "",
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "inbox"),
                req.SortOrder, req.IsActive, IsSystem: false, now, now,
                InboundMailboxAddress: null,
                NormalizeMailbox(req.OutboundMailboxAddress),
                InboundFolderId: null,
                InboundFolderName: null,
                (IReadOnlyList<Guid>)(req.AllowedStatusIds ?? Array.Empty<Guid>()),
                req.DefaultStatusId, AiAssistEnabled: req.AiAssistEnabled), ct);

            foreach (var s in normalized)
                await sources.AddAsync(created.Id, s.Mailbox, s.FolderId, s.FolderName, s.PollingEnabled, ct);
            await sources.RefreshMirrorAsync(created.Id, ct);

            var srcs = await sources.ListByQueueAsync(created.Id, ct);
            var fresh = await repo.GetQueueAsync(created.Id, ct) ?? created;
            var response = ToQueueResponse(fresh, srcs);
            await AuditWrite(audit, http, "taxonomy.queue.created", created.Id.ToString(), response);
            return Results.Created($"/api/taxonomy/queues/{created.Id}", response);
        }).WithName("CreateQueue").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/queues/{id:guid}", async (
            Guid id, [FromBody] QueueRequest req, HttpContext http,
            ITaxonomyRepository repo, IQueueInboundMailboxRepository sources,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            if (ValidateMailbox(req.OutboundMailboxAddress, "outboundMailboxAddress") is { } mErr) return mErr;
            var normalized = NormalizeInboundMailboxes(req.InboundMailboxes, out var inErr);
            if (inErr is not null) return inErr;
            // Exclusivity: a source may only conflict with one owned by a
            // *different* queue (excluding its own row when updating in place).
            foreach (var s in normalized)
            {
                var owner = await sources.FindConflictingQueueAsync(s.Mailbox, s.FolderId, s.Id, ct);
                if (owner is not null && owner != id)
                    return Results.Conflict(new { error = $"The mailbox {s.Mailbox} / {s.FolderName} is already assigned to another queue." });
            }

            var updated = await repo.UpdateQueueAsync(id, req.Name.Trim(), Slugify(req.Name),
                req.Description ?? "", Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "inbox"),
                req.SortOrder, req.IsActive,
                inboundMailboxAddress: null,
                NormalizeMailbox(req.OutboundMailboxAddress),
                inboundFolderId: null,
                inboundFolderName: null,
                (IReadOnlyList<Guid>)(req.AllowedStatusIds ?? Array.Empty<Guid>()),
                req.DefaultStatusId, req.AiAssistEnabled, ct);
            if (updated is null) return Results.NotFound();

            // Reconcile the source list against what exists.
            var existing = await sources.ListByQueueAsync(id, ct);
            var keepIds = normalized.Where(s => s.Id is { }).Select(s => s.Id!.Value).ToHashSet();
            foreach (var old in existing)
                if (!keepIds.Contains(old.Id))
                    await sources.DeleteAsync(old.Id, ct);
            var existingIds = existing.Select(e => e.Id).ToHashSet();
            foreach (var s in normalized)
            {
                if (s.Id is { } sid && existingIds.Contains(sid))
                    await sources.UpdateConfigAsync(sid, s.Mailbox, s.FolderId, s.FolderName, s.PollingEnabled, ct);
                else
                    await sources.AddAsync(id, s.Mailbox, s.FolderId, s.FolderName, s.PollingEnabled, ct);
            }
            await sources.RefreshMirrorAsync(id, ct);

            var srcs = await sources.ListByQueueAsync(id, ct);
            var fresh = await repo.GetQueueAsync(id, ct) ?? updated;
            var response = ToQueueResponse(fresh, srcs);
            await AuditWrite(audit, http, "taxonomy.queue.updated", id.ToString(), response);
            return Results.Ok(response);
        }).WithName("UpdateQueue").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/queues/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteQueueAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.queue.deleted", id);
        }).WithName("DeleteQueue").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    /// Validates + normalizes the inbound-mailbox list: each entry needs a valid
    /// mailbox and a selected folder (id + name); duplicates within the request
    /// are rejected. Returns the cleaned entries, or sets <paramref name="error"/>
    /// to a 400 result.
    private static List<NormalizedInbound> NormalizeInboundMailboxes(
        InboundMailboxInput[]? input, out IResult? error)
    {
        error = null;
        var result = new List<NormalizedInbound>();
        if (input is null || input.Length == 0) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in input)
        {
            var mailbox = NormalizeMailbox(item.Mailbox);
            if (string.IsNullOrWhiteSpace(mailbox))
            {
                error = Results.BadRequest(new { error = "Each inbound mailbox needs an email address." });
                return result;
            }
            if (ValidateMailbox(mailbox, "inboundMailboxAddress") is { } mErr)
            {
                error = mErr;
                return result;
            }
            var folderId = item.FolderId?.Trim();
            var folderName = item.FolderName?.Trim();
            if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(folderName))
            {
                error = Results.BadRequest(new { error = $"Select an inbound folder for {mailbox}." });
                return result;
            }
            if (!seen.Add($"{mailbox}|{folderId}"))
            {
                error = Results.BadRequest(new { error = $"The mailbox {mailbox} / {folderName} is listed twice." });
                return result;
            }
            result.Add(new NormalizedInbound(item.Id, mailbox!, folderId!, folderName!, item.PollingEnabled));
        }
        return result;
    }

    private sealed record NormalizedInbound(
        Guid? Id, string Mailbox, string FolderId, string FolderName, bool PollingEnabled);

    // ---------- Priorities ----------

    public sealed record PriorityRequest(
        [property: Required] string Name,
        int Level,
        string? Color,
        string? Icon,
        int SortOrder,
        bool IsActive,
        bool IsDefault);

    private static void MapPriorities(RouteGroupBuilder group)
    {
        group.MapGet("/priorities", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListPrioritiesAsync(ct)))
            .WithName("ListPriorities").WithOpenApi();

        group.MapGet("/priorities/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var p = await repo.GetPriorityAsync(id, ct);
            return p is null ? Results.NotFound() : Results.Ok(p);
        }).WithName("GetPriority").WithOpenApi();

        group.MapPost("/priorities", async (
            [FromBody] PriorityRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            var now = DateTime.UtcNow;
            var created = await repo.CreatePriorityAsync(new Priority(
                Guid.Empty, req.Name.Trim(), Slugify(req.Name), req.Level,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "flag"),
                req.SortOrder, req.IsActive, IsSystem: false, req.IsDefault, now, now), ct);
            await AuditWrite(audit, http, "taxonomy.priority.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/priorities/{created.Id}", created);
        }).WithName("CreatePriority").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/priorities/{id:guid}", async (
            Guid id, [FromBody] PriorityRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            var updated = await repo.UpdatePriorityAsync(id, req.Name.Trim(), Slugify(req.Name), req.Level,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "flag"),
                req.SortOrder, req.IsActive, req.IsDefault, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.priority.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdatePriority").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/priorities/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeletePriorityAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.priority.deleted", id);
        }).WithName("DeletePriority").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    // ---------- Statuses ----------

    public sealed record StatusRequest(
        [property: Required] string Name,
        [property: Required] string StateCategory,
        string? Color,
        string? Icon,
        int SortOrder,
        bool IsActive,
        bool IsDefault);

    private static readonly string[] AllowedStateCategories =
        ["New", "Open", "Pending", "Resolved", "Closed"];

    private static void MapStatuses(RouteGroupBuilder group)
    {
        group.MapGet("/statuses", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListStatusesAsync(ct)))
            .WithName("ListStatuses").WithOpenApi();

        group.MapGet("/statuses/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var s = await repo.GetStatusAsync(id, ct);
            return s is null ? Results.NotFound() : Results.Ok(s);
        }).WithName("GetStatus").WithOpenApi();

        group.MapPost("/statuses", async (
            [FromBody] StatusRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            if (!AllowedStateCategories.Contains(req.StateCategory))
                return Results.BadRequest(new { error = "Invalid stateCategory. Allowed: New, Open, Pending, Resolved, Closed." });
            var now = DateTime.UtcNow;
            var created = await repo.CreateStatusAsync(new Status(
                Guid.Empty, req.Name.Trim(), Slugify(req.Name), req.StateCategory,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "circle"),
                req.SortOrder, req.IsActive, IsSystem: false, req.IsDefault, now, now), ct);
            await AuditWrite(audit, http, "taxonomy.status.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/statuses/{created.Id}", created);
        }).WithName("CreateStatus").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/statuses/{id:guid}", async (
            Guid id, [FromBody] StatusRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            if (!AllowedStateCategories.Contains(req.StateCategory))
                return Results.BadRequest(new { error = "Invalid stateCategory. Allowed: New, Open, Pending, Resolved, Closed." });
            var updated = await repo.UpdateStatusAsync(id, req.Name.Trim(), Slugify(req.Name), req.StateCategory,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "circle"),
                req.SortOrder, req.IsActive, req.IsDefault, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.status.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdateStatus").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/statuses/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteStatusAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.status.deleted", id);
        }).WithName("DeleteStatus").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    // ---------- Categories ----------

    public sealed record CategoryRequest(
        [property: Required] string Name,
        Guid? ParentId,
        string? Description,
        int SortOrder,
        bool IsActive);

    private static void MapCategories(RouteGroupBuilder group)
    {
        group.MapGet("/categories", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListCategoriesAsync(ct)))
            .WithName("ListCategories").WithOpenApi();

        group.MapGet("/categories/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var c = await repo.GetCategoryAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
        }).WithName("GetCategory").WithOpenApi();

        group.MapPost("/categories", async (
            [FromBody] CategoryRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            var now = DateTime.UtcNow;
            var created = await repo.CreateCategoryAsync(new Category(
                Guid.Empty, req.ParentId, req.Name.Trim(), Slugify(req.Name),
                req.Description ?? "", req.SortOrder, req.IsActive, IsSystem: false, now, now), ct);
            await AuditWrite(audit, http, "taxonomy.category.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/categories/{created.Id}", created);
        }).WithName("CreateCategory").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/categories/{id:guid}", async (
            Guid id, [FromBody] CategoryRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name) is { } err) return err;
            if (req.ParentId == id)
                return Results.BadRequest(new { error = "A category cannot be its own parent." });
            var updated = await repo.UpdateCategoryAsync(id, req.ParentId, req.Name.Trim(), Slugify(req.Name),
                req.Description ?? "", req.SortOrder, req.IsActive, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.category.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdateCategory").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/categories/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteCategoryAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.category.deleted", id);
        }).WithName("DeleteCategory").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    // ---------- Shared helpers ----------

    private static IResult? ValidateTaxonomyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80)
            return Results.BadRequest(new { error = "Name is required and must be 80 characters or fewer." });
        return null;
    }

    private static string Slugify(string name)
    {
        var slug = name.Trim().ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"[\s]+", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "item" : slug;
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeMailbox(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static IResult? ValidateMailbox(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 320 || !Regex.IsMatch(trimmed, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return Results.BadRequest(new { error = $"{field} must be a valid email address." });
        return null;
    }

    private static async Task AuditWrite(IAuditLogger audit, HttpContext http, string eventType, string target, object payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }

    private static async Task<IResult> DeleteResultToHttp(
        DeleteResult result, HttpContext http, IAuditLogger audit, string eventType, Guid id)
    {
        switch (result)
        {
            case DeleteResult.Deleted:
                await AuditWrite(audit, http, eventType, id.ToString(), new { id });
                return Results.NoContent();
            case DeleteResult.NotFound:
                return Results.NotFound();
            case DeleteResult.SystemProtected:
                return Results.Conflict(new { error = "This is a system-protected or default row. Set another item as default first, or deactivate instead." });
            case DeleteResult.InUse:
                return Results.Conflict(new { error = "This row is still referenced by tickets. Reassign them first." });
            default:
                return Results.StatusCode(500);
        }
    }
}
