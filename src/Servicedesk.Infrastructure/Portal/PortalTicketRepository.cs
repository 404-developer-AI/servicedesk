using System.Data;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Portal;

/// One row of the portal ticket list. Deliberately narrow: no queue,
/// assignee, category, internal flags — the SELECT is the whitelist.
public sealed record PortalTicketListItem(
    Guid Id,
    long Number,
    string Subject,
    string StatusName,
    string StatusColor,
    string StateCategory,
    string PriorityName,
    string PriorityColor,
    int PriorityLevel,
    Guid RequesterContactId,
    string RequesterFirstName,
    string RequesterLastName,
    string RequesterEmail,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? ClosedUtc,
    long TotalCount);

public sealed record PortalTicketPage(IReadOnlyList<PortalTicketListItem> Items, int Total, int Page, int PageSize);

/// Scope-checked header of one ticket (null from the repository = not
/// visible to this viewer — the API answers 404, never 403).
public sealed record PortalTicketHeader(
    Guid Id,
    long Number,
    string Subject,
    string StatusName,
    string StatusColor,
    string StateCategory,
    string PriorityName,
    string PriorityColor,
    Guid RequesterContactId,
    string RequesterFirstName,
    string RequesterLastName,
    string RequesterEmail,
    string? CompanyName,
    string Source,
    Guid QueueId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? ResolvedUtc,
    DateTime? ClosedUtc,
    string BodyText,
    string? BodyHtml,
    Guid? CompanyId);

public enum PortalTicketFilter { All = 0, Open = 1, Closed = 2 }

public interface IPortalTicketRepository
{
    /// Tickets of ONE company (the customer's active company): own tickets
    /// there (Member), or every ticket there (TicketManager). Own tickets
    /// without a company ride along in every company view.
    /// <paramref name="companyId"/> must be one of the viewer's companies;
    /// null = the viewer has no company at all (own company-less tickets only).
    Task<PortalTicketPage> ListAsync(
        PortalViewer viewer, Guid? companyId, PortalTicketFilter filter, string? search, int page, int pageSize, CancellationToken ct);

    Task<PortalTicketHeader?> GetHeaderAsync(PortalViewer viewer, Guid ticketId, CancellationToken ct);

    /// True when <paramref name="eventId"/> belongs to <paramref name="ticketId"/>
    /// and is customer-visible (not internal, whitelisted type).
    Task<bool> EventIsCustomerVisibleAsync(Guid ticketId, long eventId, CancellationToken ct);

    /// True when the inbound/outbound mail row belongs to the ticket and its
    /// timeline event is customer-visible.
    Task<bool> MailMessageIsCustomerVisibleAsync(Guid ticketId, Guid mailMessageId, CancellationToken ct);

    /// The PortalMessage event the viewer authored (attachment uploads are
    /// only allowed onto your own portal messages).
    Task<bool> PortalMessageBelongsToContactAsync(Guid ticketId, long eventId, Guid contactId, CancellationToken ct);
}

public sealed class PortalTicketRepository : IPortalTicketRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PortalTicketRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// Event types a customer may see. Everything else (Note, SystemNote,
    /// Checklist*, Project*, TimeLimit*, Survey*, assignment/queue changes,
    /// …) never leaves the server. is_internal = TRUE always hides.
    public static readonly IReadOnlySet<string> CustomerVisibleEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PortalMessage", "MailReceived", "MailSent", "Comment", "StatusChange",
    };

    /// List scope — ONE company at a time (the active company in the portal
    /// header): own tickets frozen on that company (plus own tickets without
    /// any company), and every ticket of that company when the viewer is
    /// TicketManager there. Project / deleted tickets are never visible.
    private const string ListScopeWhere = """
        t.is_deleted = FALSE
        AND t.is_project = FALSE
        AND (
              (t.requester_contact_id = @ContactId
                  AND (t.company_id IS NULL OR (@CompanyId IS NOT NULL AND t.company_id = @CompanyId)))
           OR (@IsManager AND @CompanyId IS NOT NULL AND t.company_id = @CompanyId)
        )
        """;

    /// Detail scope — across all of the viewer's companies (a deep link may
    /// point at any of them; the UI switches the active company to match):
    /// own tickets at a linked company (or without company) + every ticket
    /// of a company where the viewer is TicketManager. A ticket at a company
    /// the viewer is NOT linked to stays invisible even when they requested it.
    private const string DetailScopeWhere = """
        t.is_deleted = FALSE
        AND t.is_project = FALSE
        AND (
              (t.requester_contact_id = @ContactId
                  AND (t.company_id IS NULL OR t.company_id = ANY(@AllCompanyIds)))
           OR t.company_id = ANY(@ManagerCompanyIds)
        )
        """;

    public async Task<PortalTicketPage> ListAsync(
        PortalViewer viewer, Guid? companyId, PortalTicketFilter filter, string? search, int page, int pageSize, CancellationToken ct)
    {
        if (viewer.ContactId is null)
            return new PortalTicketPage(Array.Empty<PortalTicketListItem>(), 0, page, pageSize);
        var access = companyId is null ? null : viewer.Company(companyId.Value);
        if (companyId is not null && access is null)
            return new PortalTicketPage(Array.Empty<PortalTicketListItem>(), 0, page, pageSize);

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);
        var where = ListScopeWhere;
        if (filter == PortalTicketFilter.Open) where += " AND s.state_category NOT IN ('Resolved','Closed')";
        else if (filter == PortalTicketFilter.Closed) where += " AND s.state_category IN ('Resolved','Closed')";

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        long? number = null;
        if (term is not null)
        {
            var digits = term.TrimStart('#');
            if (long.TryParse(digits, out var n)) number = n;
            where += " AND (t.subject ILIKE @Term OR (@Number IS NOT NULL AND t.number = @Number))";
        }

        var sql = $"""
            SELECT  t.id                    AS Id,
                    t.number                AS Number,
                    t.subject               AS Subject,
                    s.name                  AS StatusName,
                    s.color                 AS StatusColor,
                    s.state_category        AS StateCategory,
                    p.name                  AS PriorityName,
                    p.color                 AS PriorityColor,
                    p.level                 AS PriorityLevel,
                    t.requester_contact_id  AS RequesterContactId,
                    c.first_name            AS RequesterFirstName,
                    c.last_name             AS RequesterLastName,
                    c.email::text           AS RequesterEmail,
                    t.created_utc           AS CreatedUtc,
                    t.updated_utc           AS UpdatedUtc,
                    t.closed_utc            AS ClosedUtc,
                    COUNT(*) OVER ()        AS TotalCount
            FROM tickets t
            JOIN statuses   s ON s.id = t.status_id
            JOIN priorities p ON p.id = t.priority_id
            JOIN contacts   c ON c.id = t.requester_contact_id
            WHERE {where}
            ORDER BY t.updated_utc DESC, t.id DESC
            LIMIT @Limit OFFSET @Offset
            """;

        var args = new DynamicParameters();
        args.Add("ContactId", viewer.ContactId, DbType.Guid);
        args.Add("CompanyId", companyId, DbType.Guid);
        args.Add("IsManager", access?.IsTicketManager ?? false, DbType.Boolean);
        if (term is not null)
        {
            args.Add("Term", "%" + term + "%", DbType.String);
            args.Add("Number", number, DbType.Int64);
        }
        args.Add("Limit", pageSize, DbType.Int32);
        args.Add("Offset", (page - 1) * pageSize, DbType.Int32);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<PortalTicketListItem>(new CommandDefinition(sql, args, cancellationToken: ct))).ToList();
        var total = rows.Count > 0 ? (int)rows[0].TotalCount : 0;
        return new PortalTicketPage(rows, total, page, pageSize);
    }

    public async Task<PortalTicketHeader?> GetHeaderAsync(PortalViewer viewer, Guid ticketId, CancellationToken ct)
    {
        if (viewer.ContactId is null) return null;
        var sql = $"""
            SELECT  t.id                    AS Id,
                    t.number                AS Number,
                    t.subject               AS Subject,
                    s.name                  AS StatusName,
                    s.color                 AS StatusColor,
                    s.state_category        AS StateCategory,
                    p.name                  AS PriorityName,
                    p.color                 AS PriorityColor,
                    t.requester_contact_id  AS RequesterContactId,
                    c.first_name            AS RequesterFirstName,
                    c.last_name             AS RequesterLastName,
                    c.email::text           AS RequesterEmail,
                    co.name                 AS CompanyName,
                    t.source                AS Source,
                    t.queue_id              AS QueueId,
                    t.created_utc           AS CreatedUtc,
                    t.updated_utc           AS UpdatedUtc,
                    t.resolved_utc          AS ResolvedUtc,
                    t.closed_utc            AS ClosedUtc,
                    COALESCE(b.body_text, '') AS BodyText,
                    b.body_html             AS BodyHtml,
                    t.company_id            AS CompanyId
            FROM tickets t
            JOIN statuses   s ON s.id = t.status_id
            JOIN priorities p ON p.id = t.priority_id
            JOIN contacts   c ON c.id = t.requester_contact_id
            LEFT JOIN companies co    ON co.id = t.company_id
            LEFT JOIN ticket_bodies b ON b.ticket_id = t.id
            WHERE t.id = @TicketId AND {DetailScopeWhere}
            """;
        var args = new DynamicParameters();
        args.Add("ContactId", viewer.ContactId, DbType.Guid);
        args.Add("AllCompanyIds", viewer.AllCompanyIds);
        args.Add("ManagerCompanyIds", viewer.ManagerCompanyIds);
        args.Add("TicketId", ticketId, DbType.Guid);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PortalTicketHeader>(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    public async Task<bool> EventIsCustomerVisibleAsync(Guid ticketId, long eventId, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM ticket_events
             WHERE id = @eventId AND ticket_id = @ticketId
               AND is_internal = FALSE AND event_type = ANY(@types)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, new { eventId, ticketId, types = CustomerVisibleEventTypes.ToArray() }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> MailMessageIsCustomerVisibleAsync(Guid ticketId, Guid mailMessageId, CancellationToken ct)
    {
        // Inbound mail rows reference their event through ticket_events.metadata
        // (mail_message_id) ; outbound rows carry ticket_event_id. Either way
        // the event must be customer-visible.
        const string sql = """
            SELECT COUNT(*)
              FROM mail_messages m
              JOIN ticket_events e
                ON e.ticket_id = m.ticket_id
               AND (e.id = m.ticket_event_id
                    OR (e.metadata ->> 'mail_message_id') = m.id::text)
             WHERE m.id = @mailMessageId AND m.ticket_id = @ticketId
               AND e.is_internal = FALSE AND e.event_type = ANY(@types)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, new { mailMessageId, ticketId, types = CustomerVisibleEventTypes.ToArray() }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> PortalMessageBelongsToContactAsync(Guid ticketId, long eventId, Guid contactId, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM ticket_events
             WHERE id = @eventId AND ticket_id = @ticketId
               AND event_type = 'PortalMessage' AND author_contact_id = @contactId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, new { eventId, ticketId, contactId }, cancellationToken: ct));
        return n > 0;
    }
}
