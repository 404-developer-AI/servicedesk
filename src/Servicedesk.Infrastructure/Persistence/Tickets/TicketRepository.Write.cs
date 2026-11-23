using Servicedesk.Infrastructure.Access;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

public sealed partial class TicketRepository
{
    public async Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct)
    {
        // v0.0.39 — ticket_type_id is NOT NULL. The COALESCE-with-subquery
        // pattern lets the caller pass an explicit id (chosen by the agent
        // via a manual trigger) or fall through to the 'support' system
        // type when the caller hasn't picked one. Resolving inline avoids
        // an extra round-trip and keeps the create flow a single statement.
        const string insertTicket = """
            INSERT INTO tickets (subject, requester_contact_id, assignee_user_id, queue_id,
                                 status_id, priority_id, category_id, source,
                                 company_id, awaiting_company_assignment, company_resolved_via,
                                 pending_till_utc, ticket_type_id, is_project, is_callback)
            VALUES (@Subject, @RequesterContactId, @AssigneeUserId, @QueueId,
                    @StatusId, @PriorityId, @CategoryId, @Source,
                    @CompanyId, @AwaitingCompanyAssignment, @CompanyResolvedVia,
                    @PendingTillUtc,
                    COALESCE(@TicketTypeId,
                             (SELECT id FROM ticket_types WHERE code = 'support' LIMIT 1)),
                    @IsProject, @IsCallback)
            RETURNING id AS Id, number AS Number, subject AS Subject,
                      requester_contact_id AS RequesterContactId, assignee_user_id AS AssigneeUserId,
                      queue_id AS QueueId, status_id AS StatusId, priority_id AS PriorityId,
                      category_id AS CategoryId, source AS Source, external_ref AS ExternalRef,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc, due_utc AS DueUtc,
                      first_response_utc AS FirstResponseUtc, resolved_utc AS ResolvedUtc,
                      closed_utc AS ClosedUtc, is_deleted AS IsDeleted,
                      company_id AS CompanyId,
                      awaiting_company_assignment AS AwaitingCompanyAssignment,
                      company_resolved_via AS CompanyResolvedVia,
                      merged_into_ticket_id AS MergedIntoTicketId,
                      merged_utc AS MergedUtc,
                      merged_by_user_id AS MergedByUserId,
                      split_from_ticket_id AS SplitFromTicketId,
                      split_from_utc AS SplitFromUtc,
                      split_from_user_id AS SplitFromUserId,
                      pending_till_utc AS PendingTillUtc,
                      pending_till_next_trigger_id AS PendingTillNextTriggerId,
                      parent_ticket_id AS ParentTicketId,
                      parent_linked_utc AS ParentLinkedUtc,
                      parent_linked_by_user_id AS ParentLinkedByUserId,
                      ticket_type_id AS TicketTypeId,
                      zammad_ticket_id AS ZammadTicketId,
                      zammad_ticket_number AS ZammadTicketNumber,
                      is_project AS IsProject,
                      project_ticket_id AS ProjectTicketId,
                      project_linked_utc AS ProjectLinkedUtc,
                      project_linked_by_user_id AS ProjectLinkedByUserId,
                      project_sort_order AS ProjectSortOrder,
                      project_prompt_dismissed_utc AS ProjectPromptDismissedUtc,
                      is_callback AS IsCallback,
                      is_research AS IsResearch,
                      description_internal AS DescriptionInternal
            """;
        const string insertBody = """
            INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
            VALUES (@TicketId, @BodyText, @BodyHtml)
            """;
        const string insertEvent = """
            INSERT INTO ticket_events (ticket_id, event_type, author_contact_id, body_text, body_html, metadata)
            VALUES (@TicketId, 'Created', @AuthorContactId, NULL, NULL, COALESCE(@MetadataJson::jsonb, '{}'::jsonb))
            """;
        // v0.0.39 — optional first-note event for the manual "create
        // linked X ticket" flow. Inserted in the same transaction as
        // the ticket itself so the timeline is consistent at first
        // GET. is_internal honours the trigger config; the body is
        // pre-rendered HTML supplied by the caller.
        const string insertInitialNote = """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, body_html, is_internal, metadata)
            VALUES (@TicketId, 'Note', @AuthorUserId, @BodyHtml, @IsInternal, '{}'::jsonb)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ticket = await conn.QuerySingleAsync<Ticket>(new CommandDefinition(insertTicket, input, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertBody,
            new { TicketId = ticket.Id, input.BodyText, input.BodyHtml }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertEvent,
            new
            {
                TicketId = ticket.Id,
                AuthorContactId = input.RequesterContactId,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { source = input.Source }),
            },
            tx, cancellationToken: ct));

        if (input.InitialNote is { } note && !string.IsNullOrWhiteSpace(note.BodyHtml))
        {
            await conn.ExecuteAsync(new CommandDefinition(insertInitialNote,
                new
                {
                    TicketId = ticket.Id,
                    AuthorUserId = (Guid?)null,
                    BodyHtml = note.BodyHtml,
                    IsInternal = note.IsInternal,
                },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return ticket;
    }

    /// Agent field write: the shared <see cref="TicketFieldWriter"/> with the
    /// agent as author. Returns the fresh detail, or null when the ticket is
    /// gone. An unknown target id is a caller bug (the endpoints validate
    /// ids first) and throws, like the foreign-key error it used to be.
    public async Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct)
    {
        var result = await _fieldWriter.ApplyAsync(ticketId, update, new TicketWriteOrigin.Agent(actorUserId), ct);
        return result.Status switch
        {
            TicketFieldWriteStatus.TicketGone => null,
            TicketFieldWriteStatus.TargetNotFound =>
                throw new InvalidOperationException($"Ticket update refers to an unknown {result.MissingTarget}."),
            _ => await GetByIdAsync(ticketId, ct),
        };
    }

    public async Task<TicketDetail?> AssignCompanyAsync(Guid ticketId, Guid companyId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the ticket row and grab the previous company_id so the
        // timeline event can render "Acme → Widgets" rather than just "to Widgets".
        const string readSql = """
            SELECT id AS TicketId, company_id AS CompanyId
            FROM tickets WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var current = await conn.QueryFirstOrDefaultAsync<TicketCompanySnapshot>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (current is null) { await tx.RollbackAsync(ct); return null; }

        const string nameSql = "SELECT name FROM companies WHERE id = @id";
        var fromName = current.CompanyId.HasValue
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(nameSql, new { id = current.CompanyId.Value }, tx, cancellationToken: ct))
            : null;
        var toName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(nameSql, new { id = companyId }, tx, cancellationToken: ct));
        if (toName is null) { await tx.RollbackAsync(ct); return null; }

        const string updateSql = """
            UPDATE tickets
               SET company_id = @companyId,
                   awaiting_company_assignment = FALSE,
                   company_resolved_via = 'manual',
                   updated_utc = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { ticketId, companyId }, tx, cancellationToken: ct));
        if (rows == 0) { await tx.RollbackAsync(ct); return null; }

        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            from = current.CompanyId,
            to = companyId,
            fromName,
            toName,
            resolvedVia = "manual",
        });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'CompanyAssignment', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return await GetByIdAsync(ticketId, ct);
    }

    private sealed record TicketCompanySnapshot(Guid TicketId, Guid? CompanyId);

    public async Task<TicketDetail?> ChangeRequesterAsync(
        Guid ticketId,
        Guid newContactId,
        Guid? newCompanyId,
        bool awaitingCompanyAssignment,
        string? companyResolvedVia,
        Guid actorUserId,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string readSql = """
            SELECT id AS TicketId, requester_contact_id AS RequesterContactId, company_id AS CompanyId
            FROM tickets WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var current = await conn.QueryFirstOrDefaultAsync<TicketRequesterSnapshot>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (current is null) { await tx.RollbackAsync(ct); return null; }

        // No-op: same contact — don't touch anything so updated_utc stays put
        // and no noise event lands on the timeline.
        if (current.RequesterContactId == newContactId
            && current.CompanyId == newCompanyId)
        {
            await tx.RollbackAsync(ct);
            return await GetByIdAsync(ticketId, ct);
        }

        const string contactNameSql = """
            SELECT CONCAT_WS(' ', first_name, last_name) AS Name, email AS Email
            FROM contacts WHERE id = @id
            """;
        var fromContact = await conn.QueryFirstOrDefaultAsync<ContactNameRow>(
            new CommandDefinition(contactNameSql, new { id = current.RequesterContactId }, tx, cancellationToken: ct));
        var toContact = await conn.QueryFirstOrDefaultAsync<ContactNameRow>(
            new CommandDefinition(contactNameSql, new { id = newContactId }, tx, cancellationToken: ct));
        if (toContact is null) { await tx.RollbackAsync(ct); return null; }

        const string companyNameSql = "SELECT name FROM companies WHERE id = @id";
        var fromCompanyName = current.CompanyId.HasValue
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                companyNameSql, new { id = current.CompanyId.Value }, tx, cancellationToken: ct))
            : null;
        var toCompanyName = newCompanyId.HasValue
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                companyNameSql, new { id = newCompanyId.Value }, tx, cancellationToken: ct))
            : null;

        const string updateSql = """
            UPDATE tickets
               SET requester_contact_id = @newContactId,
                   company_id = @newCompanyId,
                   awaiting_company_assignment = @awaiting,
                   company_resolved_via = @resolvedVia,
                   updated_utc = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            ticketId,
            newContactId,
            newCompanyId,
            awaiting = awaitingCompanyAssignment,
            resolvedVia = companyResolvedVia,
        }, tx, cancellationToken: ct));
        if (rows == 0) { await tx.RollbackAsync(ct); return null; }

        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            fromContactId = current.RequesterContactId,
            toContactId = newContactId,
            fromName = fromContact?.Name,
            toName = toContact.Name,
            fromEmail = fromContact?.Email,
            toEmail = toContact.Email,
            fromCompanyId = current.CompanyId,
            toCompanyId = newCompanyId,
            fromCompanyName,
            toCompanyName,
            resolvedVia = companyResolvedVia,
        });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'RequesterChange', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return await GetByIdAsync(ticketId, ct);
    }

    private sealed record TicketRequesterSnapshot(Guid TicketId, Guid RequesterContactId, Guid? CompanyId);
    private sealed record ContactNameRow(string? Name, string? Email);

}
