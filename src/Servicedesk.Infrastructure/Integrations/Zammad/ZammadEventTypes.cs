namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Audit event-type strings for the Zammad migration link (v0.0.41). Two
/// audiences mirror the Telavox / Adsolut split:
/// <list type="bullet">
/// <item><c>audit_log</c> — credential/admin actions (token set, base URL
/// changed). Hash-chained security trail.</item>
/// <item><c>integration_audit</c> — outbound HTTP calls (Zammad API hits)
/// with latency + http_status + upstream error.</item>
/// </list>
public static class ZammadEventTypes
{
    /// Integration name written to <c>integration_audit.integration</c>.
    public const string Integration = "zammad";

    // ---- audit_log (security trail) -----------------------------------

    /// Admin updated the API token in protected_secrets. Only the fact of
    /// change is recorded, never the token itself.
    public const string TokenUpdated = "integration.zammad.token.updated";

    /// Admin cleared the API token. Integration is now inert until a new
    /// token is supplied.
    public const string TokenDeleted = "integration.zammad.token.deleted";

    /// Admin changed the base URL of the source Zammad instance. Payload
    /// carries the new URL so a typo is traceable.
    public const string BaseUrlUpdated = "integration.zammad.base_url.updated";

    // ---- integration_audit (operational log) --------------------------

    /// Composite Test-connection action — one row per click on the
    /// integration page. Aggregates the /users/me + /version calls so the
    /// admin sees a single line per attempt.
    public const string TestConnection = "test_connection";

    /// GET /api/v1/users/me — verifies the token and returns the agent the
    /// token belongs to.
    public const string UsersMe = "api.users.me";

    /// GET /api/v1/version — returns the Zammad server version. Public on
    /// many installs but the admin still appreciates seeing it correlate
    /// with their /users/me success.
    public const string VersionGet = "api.version.get";

    /// GET /api/v1/groups — feeds the picker's multi-select group filter.
    public const string GroupsList = "api.groups.list";

    /// GET /api/v1/ticket_states — feeds the picker's multi-select state
    /// filter.
    public const string StatesList = "api.states.list";

    /// GET /api/v1/ticket_priorities — feeds the priority-mapping table
    /// on the integration page (v0.0.41 phase 3).
    public const string PrioritiesList = "api.priorities.list";

    /// GET /api/v1/tickets/search — paginated ticket search backing the
    /// admin picker. One row per click on Search or per page-change.
    public const string TicketsSearch = "api.tickets.search";

    /// GET /api/v1/tickets/{id} — single-ticket fetch used by the
    /// dry-run worker. One row per ticket processed.
    public const string TicketGet = "api.tickets.get";

    /// GET /api/v1/users/{id} — used by the Create-contact dialog to
    /// pre-fill name fields from Zammad's user record.
    public const string UserGet = "api.users.get";

    /// GET /api/v1/ticket_articles/by_ticket/{id} — full article list
    /// for one ticket. Walked once per ticket during the real import.
    public const string ArticlesList = "api.articles.list";

    /// GET /api/v1/ticket_attachment/{ticketId}/{articleId}/{attachmentId} —
    /// raw bytes for one Zammad attachment. One row per attachment fetched
    /// during the real import; payload carries the local attachment row id
    /// + byte count, never the bytes themselves.
    public const string AttachmentFetch = "api.attachment.fetch";

    // ---- mapping CRUD (audit_log security trail) ----------------------

    /// Admin set or updated a Zammad-group → queue mapping. Payload
    /// records both the Zammad-id + name and the local queue id.
    public const string GroupMappingUpdated = "integration.zammad.mapping.group.updated";

    /// Admin removed a Zammad-group → queue mapping.
    public const string GroupMappingDeleted = "integration.zammad.mapping.group.deleted";

    /// Admin set or updated a Zammad-state → status mapping.
    public const string StateMappingUpdated = "integration.zammad.mapping.state.updated";

    /// Admin removed a Zammad-state → status mapping.
    public const string StateMappingDeleted = "integration.zammad.mapping.state.deleted";

    /// Admin set or updated a Zammad-priority → priority mapping.
    public const string PriorityMappingUpdated = "integration.zammad.mapping.priority.updated";

    /// Admin removed a Zammad-priority → priority mapping.
    public const string PriorityMappingDeleted = "integration.zammad.mapping.priority.deleted";

    // ---- dry-run lifecycle (audit_log security trail) -----------------

    /// Admin started a dry-run. Payload includes the source filter +
    /// ticket selection so a later admin can see what was attempted.
    public const string DryRunStarted = "integration.zammad.dry_run.started";

    /// Worker finished a dry-run (any terminal status). Payload carries
    /// the run-id + final totals.
    public const string DryRunFinished = "integration.zammad.dry_run.finished";

    /// Admin requested cancellation of a running dry-run.
    public const string DryRunCancelled = "integration.zammad.dry_run.cancelled";

    // ---- real-import lifecycle (audit_log security trail) ------------

    /// Admin started a real import from a prior dry-run. Payload carries
    /// the parent dry-run-id + import-run-id so the trail can hop between
    /// rows.
    public const string ImportStarted = "integration.zammad.import.started";

    /// Worker finished a real import. Payload carries the final totals.
    public const string ImportFinished = "integration.zammad.import.finished";

    /// Admin cancelled a running real import.
    public const string ImportCancelled = "integration.zammad.import.cancelled";
}
