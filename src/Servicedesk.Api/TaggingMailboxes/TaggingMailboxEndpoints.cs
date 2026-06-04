using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.TaggingMailboxes;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.TaggingMailboxes;

namespace Servicedesk.Api.TaggingMailboxes;

/// HTTP surface for tagging-only mailboxes — login-less @@-mention targets
/// that receive a notification mail when tagged in a note / reply / outbound
/// mail.
///
/// <list type="bullet">
/// <item><b>Admin</b> — CRUD under <c>/api/settings/tagging-mailboxes</c>,
/// surfaced as the first card on Settings → Users.</item>
/// <item><b>Agent</b> — active-only typeahead at
/// <c>/api/tagging-mailboxes/search</c> that the @@-picker merges with the
/// agent results.</item>
/// </list>
public static class TaggingMailboxEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;

    public static IEndpointRouteBuilder MapTaggingMailboxEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdminEndpoints(app);
        MapAgentEndpoints(app);
        return app;
    }

    private static void MapAdminEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/tagging-mailboxes")
            .WithTags("TaggingMailboxes")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (ITaggingMailboxRepository repo, CancellationToken ct) =>
        {
            var rows = await repo.ListAsync(ct);
            return Results.Ok(rows.Select(MapDto));
        }).WithName("ListTaggingMailboxes").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] UpsertRequest req, HttpContext http,
            ITaggingMailboxRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var (err, name, email) = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            Guid id;
            try
            {
                id = await repo.CreateAsync(name!, email!, req.IsActive ?? true, ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
            {
                return Results.Conflict(new { error = "A tagging mailbox with that e-mail address already exists." });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "tagging_mailbox.create",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { email, isActive = req.IsActive ?? true }));

            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/tagging-mailboxes/{id}", MapDto(created!));
        }).WithName("CreateTaggingMailbox").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] UpsertRequest req, HttpContext http,
            ITaggingMailboxRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var (err, name, email) = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            try
            {
                await repo.UpdateAsync(id, name!, email!, req.IsActive ?? existing.IsActive, ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
            {
                return Results.Conflict(new { error = "Another tagging mailbox already uses that e-mail address." });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "tagging_mailbox.update",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { email, isActive = req.IsActive ?? existing.IsActive }));

            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapDto(updated!));
        }).WithName("UpdateTaggingMailbox").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ITaggingMailboxRepository repo,
            IAuditLogger audit, CancellationToken ct) =>
        {
            // Hard delete is safe: tagging mailboxes are not FK-referenced.
            // Past mentions are baked into the ticket-event HTML / metadata as
            // plain ids — the row is only a directory entry, not a live link.
            var deleted = await repo.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "tagging_mailbox.delete",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()));

            return Results.NoContent();
        }).WithName("DeleteTaggingMailbox").WithOpenApi();
    }

    private static void MapAgentEndpoints(IEndpointRouteBuilder app)
    {
        // The @@-picker hits this on every keystroke. Agent-accessible (same
        // posture as /api/users/agents/search) so agents can tag a mailbox.
        app.MapGet("/api/tagging-mailboxes/search", async (
            string? q, int? limit, ITaggingMailboxRepository repo, CancellationToken ct) =>
        {
            var rows = await repo.SearchActiveAsync(q, limit ?? 20, ct);
            return Results.Ok(rows.Select(MapSearchDto));
        })
            .WithTags("TaggingMailboxesAgent")
            .WithName("SearchTaggingMailboxes")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent)
            .WithOpenApi();
    }

    public sealed record UpsertRequest(
        [Required] string? Name,
        [Required] string? Email,
        bool? IsActive);

    private static (string? Error, string? Name, string? Email) Validate(UpsertRequest req)
    {
        if (req is null) return ("Body is required.", null, null);

        var name = req.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxNameLength)
            return ($"Name is required and must be ≤{MaxNameLength} characters.", null, null);

        var email = req.Email?.Trim() ?? string.Empty;
        if (email.Length == 0 || email.Length > MaxEmailLength)
            return ($"E-mail is required and must be ≤{MaxEmailLength} characters.", null, null);
        if (!MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase))
            return ("E-mail must be a valid address.", null, null);

        return (null, name, email);
    }

    private static object MapDto(TaggingMailbox m) => new
    {
        id = m.Id,
        name = HtmlEncoder.Default.Encode(m.Name),
        email = m.Email,
        isActive = m.IsActive,
        createdUtc = m.CreatedUtc,
        updatedUtc = m.UpdatedUtc,
    };

    /// Slim DTO for the @@-picker — just what the popover renders.
    private static object MapSearchDto(TaggingMailbox m) => new
    {
        id = m.Id,
        name = HtmlEncoder.Default.Encode(m.Name),
        email = m.Email,
    };
}
