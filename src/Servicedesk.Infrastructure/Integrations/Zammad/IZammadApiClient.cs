namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Zammad API surface used by the migration link (v0.0.41). Phase 1 only
/// needs the test-connection probe; the broader ticket / article / user
/// surfaces land in later phases against this same interface.
public interface IZammadApiClient
{
    /// Two-call probe used by the integration page's "Test connection"
    /// button:
    /// <list type="number">
    /// <item><c>GET /api/v1/users/me</c> — proves the token is valid and
    /// returns the agent the token belongs to.</item>
    /// <item><c>GET /api/v1/version</c> — returns the Zammad server
    /// version. Permissive: a 401/403 on this call does not fail the
    /// whole test (some installs lock /version down even though /users/me
    /// succeeded).</item>
    /// </list>
    /// Throws <see cref="ZammadApiException"/> on any condition that
    /// makes <c>/users/me</c> fail (network, 401, 5xx, missing config).
    /// Latency in the result is wall-clock across both calls.
    Task<ZammadTestConnectionResult> TestConnectionAsync(CancellationToken ct);

    /// <c>GET /api/v1/groups</c> — full list of groups (Zammad's
    /// queue-equivalent). Feeds the picker's multi-select group filter.
    /// Caller is expected to cache the result for the lifetime of the
    /// admin's session.
    Task<IReadOnlyList<ZammadGroup>> ListGroupsAsync(CancellationToken ct);

    /// <c>GET /api/v1/ticket_states</c> — full list of ticket states.
    /// Caller is expected to cache.
    Task<IReadOnlyList<ZammadState>> ListStatesAsync(CancellationToken ct);

    /// <c>GET /api/v1/ticket_priorities</c> — full list of ticket
    /// priorities (Zammad ships with three by default). Drives the
    /// priority-mapping table on the integration page (v0.0.41 phase 3).
    Task<IReadOnlyList<ZammadPriority>> ListPrioritiesAsync(CancellationToken ct);

    /// <c>GET /api/v1/tickets/search</c> with a composed Elasticsearch
    /// query built from the structured filters. Calls Zammad with
    /// <c>expand=true</c> AND <c>with_total_count=true</c> so the
    /// response carries denormalised relations + a server-side match
    /// total. The implementation tolerates both response shapes Zammad
    /// uses in practice (bare expanded ticket array, and the
    /// <c>{ tickets, assets }</c> envelope).
    Task<ZammadTicketSearchPage> SearchTicketsAsync(
        ZammadTicketSearchQuery query,
        CancellationToken ct);

    /// <c>GET /api/v1/tickets/{id}</c> with <c>expand=true</c>. Surface
    /// the worker needs for the dry-run + (later) real import: full
    /// ticket plus the resolved customer relation (id+email+name).
    /// Returns null when Zammad responds 404 — the worker treats that
    /// as a soft skip rather than crashing the whole run.
    Task<ZammadTicket?> GetTicketAsync(long ticketId, CancellationToken ct);

    /// <c>GET /api/v1/users/{id}</c>. Used by the Create-contact dialog
    /// to pre-fill first/last name. Returns null on 404 so the dialog
    /// can fall back to empty fields.
    Task<ZammadUser?> GetUserAsync(long userId, CancellationToken ct);

    /// <c>GET /api/v1/ticket_articles/by_ticket/{ticketId}</c>. Full
    /// article list (Zammad's "timeline") in upload order. The real-
    /// import worker (v0.0.41 phase 4) walks this list once per ticket:
    /// the first article becomes <c>ticket_bodies</c>, subsequent
    /// articles become <c>ticket_events</c>. Returns an empty list when
    /// Zammad responds 404 — same soft-skip pattern as <see cref="GetTicketAsync"/>.
    Task<IReadOnlyList<ZammadArticle>> ListArticlesAsync(long ticketId, CancellationToken ct);

    /// <c>GET /api/v1/ticket_attachment/{ticketId}/{articleId}/{attachmentId}</c>
    /// — raw bytes for one attachment listed in the per-article manifest.
    /// Returns a read-open stream; the caller is responsible for disposing.
    /// Throws <see cref="ZammadApiException"/> on any non-2xx (404 included
    /// — unlike the metadata endpoints, the importer treats a missing
    /// attachment as a per-row failure rather than a soft skip so the
    /// admin can see the gap in the import-record reasons list).
    Task<Stream> FetchAttachmentBytesAsync(
        long ticketId,
        long articleId,
        long attachmentId,
        CancellationToken ct);
}
