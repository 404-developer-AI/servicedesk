using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Checklists;

public interface ITicketChecklistRepository
{
    Task<IReadOnlyList<TicketChecklistView>> ListForTicketAsync(Guid ticketId, CancellationToken ct);
    Task<TicketChecklistRow?> GetChecklistAsync(Guid checklistId, CancellationToken ct);
    Task<int> CountForTicketAsync(Guid ticketId, CancellationToken ct);
    Task<int> CountItemsAsync(Guid checklistId, CancellationToken ct);

    /// Expands a template definition into checklist + section + item rows
    /// (one transaction), recomputes the counters and returns the new id.
    Task<Guid> AttachAsync(
        Guid ticketId, Guid? templateId, string name, string description, bool blockClose,
        ChecklistTemplateDefinition definition, Guid userId, CancellationToken ct);

    Task<bool> DetachAsync(Guid checklistId, CancellationToken ct);

    Task<TicketChecklistItem?> GetItemAsync(Guid itemId, CancellationToken ct);

    /// Sets the item state, logs the change (only when the state actually
    /// changes; a comment-only call with the same state is a no-op here —
    /// use <see cref="AddCommentAsync"/>) and recomputes the counters.
    /// Returns null when the item does not exist.
    Task<ChecklistItemStateChange?> SetItemStateAsync(
        Guid itemId, string newState, string naReason, string comment, Guid userId, CancellationToken ct);

    Task<bool> AddCommentAsync(Guid itemId, string comment, Guid userId, CancellationToken ct);

    Task<Guid?> AddItemAsync(Guid checklistId, Guid? sectionId, ChecklistTemplateItem item, Guid userId, CancellationToken ct);
    Task<bool> UpdateItemAsync(Guid itemId, ChecklistTemplateItem item, Guid userId, CancellationToken ct);
    Task<bool> RemoveItemAsync(Guid itemId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<TicketChecklistItemEvent>> ListItemEventsAsync(Guid itemId, CancellationToken ct);

    /// Attached checklists with block_close that still have required open
    /// items — the input of the close guard.
    Task<IReadOnlyList<ChecklistBlocker>> GetBlockersAsync(Guid ticketId, CancellationToken ct);
}

public sealed class TicketChecklistRepository : ITicketChecklistRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketChecklistRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string ChecklistSelect = """
        SELECT c.id                  AS Id,
               c.ticket_id           AS TicketId,
               c.template_id         AS TemplateId,
               c.name                AS Name,
               c.description         AS Description,
               c.block_close         AS BlockClose,
               c.sort_order          AS SortOrder,
               c.attached_by_user_id AS AttachedByUserId,
               COALESCE(NULLIF(u.display_name, ''), u.email) AS AttachedByName,
               c.attached_utc        AS AttachedUtc,
               c.completed_utc       AS CompletedUtc,
               c.required_total      AS RequiredTotal,
               c.required_done       AS RequiredDone,
               c.total_items         AS TotalItems,
               c.done_items          AS DoneItems,
               c.touched             AS Touched
        FROM ticket_checklists c
        LEFT JOIN users u ON u.id = c.attached_by_user_id
        """;

    private const string ItemSelect = """
        SELECT i.id                       AS Id,
               i.checklist_id             AS ChecklistId,
               c.ticket_id                AS TicketId,
               i.section_id               AS SectionId,
               i.title                    AS Title,
               i.description              AS Description,
               i.team_label               AS TeamLabel,
               i.timing_label             AS TimingLabel,
               i.link_url                 AS LinkUrl,
               i.link_label               AS LinkLabel,
               i.is_required              AS IsRequired,
               i.sort_order               AS SortOrder,
               i.is_ad_hoc                AS IsAdHoc,
               i.added_by_user_id         AS AddedByUserId,
               COALESCE(NULLIF(au.display_name, ''), au.email) AS AddedByName,
               i.state                    AS State,
               i.state_changed_utc        AS StateChangedUtc,
               i.state_changed_by_user_id AS StateChangedByUserId,
               COALESCE(NULLIF(su.display_name, ''), su.email) AS StateChangedByName,
               i.na_reason                AS NaReason,
               i.comment_count            AS CommentCount,
               i.created_utc              AS CreatedUtc
        FROM ticket_checklist_items i
        JOIN ticket_checklists c ON c.id = i.checklist_id
        LEFT JOIN users au ON au.id = i.added_by_user_id
        LEFT JOIN users su ON su.id = i.state_changed_by_user_id
        """;

    public async Task<IReadOnlyList<TicketChecklistView>> ListForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var checklists = (await conn.QueryAsync<TicketChecklistRow>(new CommandDefinition(
            ChecklistSelect + " WHERE c.ticket_id = @ticketId ORDER BY c.sort_order, c.attached_utc, c.id",
            new { ticketId }, cancellationToken: ct))).ToList();
        if (checklists.Count == 0) return Array.Empty<TicketChecklistView>();

        var ids = checklists.Select(c => c.Id).ToArray();
        var sections = (await conn.QueryAsync<TicketChecklistSection>(new CommandDefinition("""
            SELECT id AS Id, checklist_id AS ChecklistId, title AS Title, sort_order AS SortOrder
            FROM ticket_checklist_sections
            WHERE checklist_id = ANY(@ids)
            ORDER BY sort_order, id
            """, new { ids }, cancellationToken: ct))).ToList();
        var items = (await conn.QueryAsync<TicketChecklistItem>(new CommandDefinition(
            ItemSelect + " WHERE i.checklist_id = ANY(@ids) ORDER BY i.sort_order, i.created_utc, i.id",
            new { ids }, cancellationToken: ct))).ToList();

        var sectionsBy = sections.GroupBy(s => s.ChecklistId).ToDictionary(g => g.Key, g => (IReadOnlyList<TicketChecklistSection>)g.ToList());
        var itemsBy = items.GroupBy(i => i.ChecklistId).ToDictionary(g => g.Key, g => (IReadOnlyList<TicketChecklistItem>)g.ToList());
        return checklists.Select(c => new TicketChecklistView(
            c,
            sectionsBy.GetValueOrDefault(c.Id) ?? Array.Empty<TicketChecklistSection>(),
            itemsBy.GetValueOrDefault(c.Id) ?? Array.Empty<TicketChecklistItem>())).ToList();
    }

    public async Task<TicketChecklistRow?> GetChecklistAsync(Guid checklistId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TicketChecklistRow>(new CommandDefinition(
            ChecklistSelect + " WHERE c.id = @checklistId", new { checklistId }, cancellationToken: ct));
    }

    public async Task<int> CountForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM ticket_checklists WHERE ticket_id = @ticketId", new { ticketId }, cancellationToken: ct));
    }

    public async Task<int> CountItemsAsync(Guid checklistId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM ticket_checklist_items WHERE checklist_id = @checklistId", new { checklistId }, cancellationToken: ct));
    }

    public async Task<Guid> AttachAsync(
        Guid ticketId, Guid? templateId, string name, string description, bool blockClose,
        ChecklistTemplateDefinition definition, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var checklistId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO ticket_checklists (ticket_id, template_id, name, description, block_close, sort_order, attached_by_user_id)
            VALUES (@ticketId, @templateId, @name, @description, @blockClose,
                    COALESCE((SELECT MAX(sort_order) + 1 FROM ticket_checklists WHERE ticket_id = @ticketId), 0),
                    @userId)
            RETURNING id
            """, new { ticketId, templateId, name, description, blockClose, userId }, transaction: tx, cancellationToken: ct));

        var sectionOrder = 0;
        var itemOrder = 0;
        foreach (var section in definition.Sections)
        {
            Guid? sectionId = null;
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                sectionId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                    INSERT INTO ticket_checklist_sections (checklist_id, title, sort_order)
                    VALUES (@checklistId, @title, @sortOrder)
                    RETURNING id
                    """, new { checklistId, title = section.Title.Trim(), sortOrder = sectionOrder++ },
                    transaction: tx, cancellationToken: ct));
            }
            foreach (var item in section.Items)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO ticket_checklist_items
                        (checklist_id, section_id, title, description, team_label, timing_label, link_url, link_label, is_required, sort_order)
                    VALUES (@checklistId, @sectionId, @title, @description, @teamLabel, @timingLabel, @linkUrl, @linkLabel, @isRequired, @sortOrder)
                    """, new
                {
                    checklistId, sectionId,
                    title = item.Title, description = item.Description,
                    teamLabel = item.TeamLabel, timingLabel = item.TimingLabel,
                    linkUrl = item.LinkUrl, linkLabel = item.LinkLabel,
                    isRequired = item.IsRequired, sortOrder = itemOrder++,
                }, transaction: tx, cancellationToken: ct));
            }
        }

        await RecountAsync(conn, tx, checklistId, ticketId, touch: false, ct);
        await tx.CommitAsync(ct);
        return checklistId;
    }

    public async Task<bool> DetachAsync(Guid checklistId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ticketId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "DELETE FROM ticket_checklists WHERE id = @checklistId RETURNING ticket_id",
            new { checklistId }, transaction: tx, cancellationToken: ct));
        if (ticketId is null) { await tx.RollbackAsync(ct); return false; }
        await RecountTicketAsync(conn, tx, ticketId.Value, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<TicketChecklistItem?> GetItemAsync(Guid itemId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TicketChecklistItem>(new CommandDefinition(
            ItemSelect + " WHERE i.id = @itemId", new { itemId }, cancellationToken: ct));
    }

    public async Task<ChecklistItemStateChange?> SetItemStateAsync(
        Guid itemId, string newState, string naReason, string comment, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the item row so two agents ticking the same box serialize and
        // the second sees the first's state (and logs nothing if equal).
        var before = await conn.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition("""
            SELECT i.id AS Id, i.checklist_id AS ChecklistId, c.ticket_id AS TicketId, c.name AS ChecklistName,
                   i.state AS State, (c.completed_utc IS NOT NULL) AS IsComplete
            FROM ticket_checklist_items i
            JOIN ticket_checklists c ON c.id = i.checklist_id
            WHERE i.id = @itemId
            FOR UPDATE OF i
            """, new { itemId }, transaction: tx, cancellationToken: ct));
        if (before is null) { await tx.RollbackAsync(ct); return null; }

        if (before.State == newState)
        {
            await tx.RollbackAsync(ct);
            return new ChecklistItemStateChange(false, before.ChecklistId, before.TicketId, before.ChecklistName,
                before.State, newState, before.IsComplete, before.IsComplete);
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_checklist_items
               SET state = @newState,
                   state_changed_utc = now(),
                   state_changed_by_user_id = @userId,
                   na_reason = CASE WHEN @newState = 'na' THEN @naReason ELSE '' END
             WHERE id = @itemId
            """, new { itemId, newState, userId, naReason }, transaction: tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_checklist_item_events (item_id, checklist_id, ticket_id, user_id, kind, from_state, to_state, comment)
            VALUES (@itemId, @checklistId, @ticketId, @userId, 'state_change', @fromState, @toState, @comment)
            """, new
        {
            itemId, checklistId = before.ChecklistId, ticketId = before.TicketId, userId,
            fromState = before.State, toState = newState,
            comment = newState == ChecklistItemState.NotApplicable && comment.Length == 0 ? naReason : comment,
        }, transaction: tx, cancellationToken: ct));

        var isComplete = await RecountAsync(conn, tx, before.ChecklistId, before.TicketId, touch: true, ct);
        await tx.CommitAsync(ct);
        return new ChecklistItemStateChange(true, before.ChecklistId, before.TicketId, before.ChecklistName,
            before.State, newState, before.IsComplete, isComplete);
    }

    public async Task<bool> AddCommentAsync(Guid itemId, string comment, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = await conn.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition("""
            SELECT i.id AS Id, i.checklist_id AS ChecklistId, c.ticket_id AS TicketId, c.name AS ChecklistName,
                   i.state AS State, (c.completed_utc IS NOT NULL) AS IsComplete
            FROM ticket_checklist_items i JOIN ticket_checklists c ON c.id = i.checklist_id
            WHERE i.id = @itemId
            """, new { itemId }, transaction: tx, cancellationToken: ct));
        if (ids is null) { await tx.RollbackAsync(ct); return false; }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_checklist_item_events (item_id, checklist_id, ticket_id, user_id, kind, comment)
            VALUES (@itemId, @checklistId, @ticketId, @userId, 'comment', @comment);
            UPDATE ticket_checklist_items SET comment_count = comment_count + 1 WHERE id = @itemId;
            UPDATE ticket_checklists SET touched = TRUE WHERE id = @checklistId;
            """, new { itemId, checklistId = ids.ChecklistId, ticketId = ids.TicketId, userId, comment },
            transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<Guid?> AddItemAsync(Guid checklistId, Guid? sectionId, ChecklistTemplateItem item, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ticketId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT ticket_id FROM ticket_checklists WHERE id = @checklistId FOR UPDATE",
            new { checklistId }, transaction: tx, cancellationToken: ct));
        if (ticketId is null) { await tx.RollbackAsync(ct); return null; }

        // Section must belong to this checklist; otherwise the item lands
        // ungrouped rather than in a foreign checklist's section.
        if (sectionId.HasValue)
        {
            var ok = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM ticket_checklist_sections WHERE id = @sectionId AND checklist_id = @checklistId)",
                new { sectionId, checklistId }, transaction: tx, cancellationToken: ct));
            if (!ok) sectionId = null;
        }

        // Append after the last item of the target section (or of the whole
        // checklist when ungrouped) so it shows up where the agent added it.
        var itemId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            WITH anchor AS (
                SELECT COALESCE(MAX(sort_order), -1) AS last_in_section
                FROM ticket_checklist_items
                WHERE checklist_id = @checklistId
                  AND ((@sectionId::uuid IS NULL AND section_id IS NULL) OR section_id = @sectionId)
            ),
            shifted AS (
                UPDATE ticket_checklist_items
                   SET sort_order = sort_order + 1
                 WHERE checklist_id = @checklistId
                   AND sort_order > (SELECT last_in_section FROM anchor)
                   AND (SELECT last_in_section FROM anchor) >= 0
                RETURNING 1
            )
            INSERT INTO ticket_checklist_items
                (checklist_id, section_id, title, description, team_label, timing_label, link_url, link_label,
                 is_required, sort_order, is_ad_hoc, added_by_user_id)
            VALUES (@checklistId, @sectionId, @title, @description, @teamLabel, @timingLabel, @linkUrl, @linkLabel,
                    @isRequired,
                    CASE WHEN (SELECT last_in_section FROM anchor) >= 0
                         THEN (SELECT last_in_section FROM anchor) + 1
                         ELSE COALESCE((SELECT MAX(sort_order) + 1 FROM ticket_checklist_items WHERE checklist_id = @checklistId), 0)
                    END,
                    TRUE, @userId)
            RETURNING id
            """, new
        {
            checklistId, sectionId,
            title = item.Title, description = item.Description,
            teamLabel = item.TeamLabel, timingLabel = item.TimingLabel,
            linkUrl = item.LinkUrl, linkLabel = item.LinkLabel,
            isRequired = item.IsRequired, userId,
        }, transaction: tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_checklist_item_events (item_id, checklist_id, ticket_id, user_id, kind, comment)
            VALUES (@itemId, @checklistId, @ticketId, @userId, 'item_added', @title)
            """, new { itemId, checklistId, ticketId, userId, title = item.Title }, transaction: tx, cancellationToken: ct));

        await RecountAsync(conn, tx, checklistId, ticketId.Value, touch: true, ct);
        await tx.CommitAsync(ct);
        return itemId;
    }

    public async Task<bool> UpdateItemAsync(Guid itemId, ChecklistTemplateItem item, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = await conn.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition("""
            SELECT i.id AS Id, i.checklist_id AS ChecklistId, c.ticket_id AS TicketId, c.name AS ChecklistName,
                   i.state AS State, (c.completed_utc IS NOT NULL) AS IsComplete
            FROM ticket_checklist_items i JOIN ticket_checklists c ON c.id = i.checklist_id
            WHERE i.id = @itemId FOR UPDATE OF i
            """, new { itemId }, transaction: tx, cancellationToken: ct));
        if (ids is null) { await tx.RollbackAsync(ct); return false; }
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_checklist_items
               SET title = @title, description = @description, team_label = @teamLabel, timing_label = @timingLabel,
                   link_url = @linkUrl, link_label = @linkLabel
             WHERE id = @itemId;
            INSERT INTO ticket_checklist_item_events (item_id, checklist_id, ticket_id, user_id, kind, comment)
            VALUES (@itemId, @checklistId, @ticketId, @userId, 'item_edited', @title);
            """, new
        {
            itemId, checklistId = ids.ChecklistId, ticketId = ids.TicketId, userId,
            title = item.Title, description = item.Description,
            teamLabel = item.TeamLabel, timingLabel = item.TimingLabel,
            linkUrl = item.LinkUrl, linkLabel = item.LinkLabel,
        }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RemoveItemAsync(Guid itemId, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = await conn.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition("""
            SELECT i.id AS Id, i.checklist_id AS ChecklistId, c.ticket_id AS TicketId, c.name AS ChecklistName,
                   i.state AS State, (c.completed_utc IS NOT NULL) AS IsComplete
            FROM ticket_checklist_items i JOIN ticket_checklists c ON c.id = i.checklist_id
            WHERE i.id = @itemId FOR UPDATE OF i
            """, new { itemId }, transaction: tx, cancellationToken: ct));
        if (ids is null) { await tx.RollbackAsync(ct); return false; }
        // The item's own log cascades away with the row; the removal itself
        // is recorded in the audit log by the service.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ticket_checklist_items WHERE id = @itemId", new { itemId }, transaction: tx, cancellationToken: ct));
        await RecountAsync(conn, tx, ids.ChecklistId, ids.TicketId, touch: false, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<TicketChecklistItemEvent>> ListItemEventsAsync(Guid itemId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TicketChecklistItemEvent>(new CommandDefinition("""
            SELECT e.id          AS Id,
                   e.item_id     AS ItemId,
                   e.user_id     AS UserId,
                   COALESCE(NULLIF(u.display_name, ''), u.email) AS UserName,
                   e.kind        AS Kind,
                   e.from_state  AS FromState,
                   e.to_state    AS ToState,
                   e.comment     AS Comment,
                   e.created_utc AS CreatedUtc
            FROM ticket_checklist_item_events e
            LEFT JOIN users u ON u.id = e.user_id
            WHERE e.item_id = @itemId
            ORDER BY e.created_utc DESC, e.id DESC
            LIMIT 500
            """, new { itemId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ChecklistBlocker>> GetBlockersAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BlockerRow>(new CommandDefinition("""
            SELECT id AS ChecklistId, name AS Name, (required_total - required_done) AS OpenRequired
            FROM ticket_checklists
            WHERE ticket_id = @ticketId AND block_close = TRUE AND required_done < required_total
            ORDER BY sort_order, id
            """, new { ticketId }, cancellationToken: ct));
        return rows.Select(r => new ChecklistBlocker(r.ChecklistId, r.Name, r.OpenRequired)).ToList();
    }

    /// Recomputes one checklist's counters + completion flag from its items,
    /// then the ticket's denormalized totals. Returns "is complete now".
    private static async Task<bool> RecountAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid checklistId, Guid ticketId, bool touch, CancellationToken ct)
    {
        var isComplete = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            WITH s AS (
                SELECT COUNT(*) FILTER (WHERE is_required)                          AS rt,
                       COUNT(*) FILTER (WHERE is_required AND state <> 'open')      AS rd,
                       COUNT(*)                                                     AS ti,
                       COUNT(*) FILTER (WHERE state <> 'open')                      AS di
                FROM ticket_checklist_items
                WHERE checklist_id = @checklistId
            )
            UPDATE ticket_checklists c
               SET required_total = s.rt,
                   required_done  = s.rd,
                   total_items    = s.ti,
                   done_items     = s.di,
                   touched        = c.touched OR @touch,
                   completed_utc  = CASE
                       WHEN (s.rt > 0 AND s.rd = s.rt) OR (s.rt = 0 AND s.ti > 0 AND s.di = s.ti)
                           THEN COALESCE(c.completed_utc, now())
                       ELSE NULL END
              FROM s
             WHERE c.id = @checklistId
            RETURNING (c.completed_utc IS NOT NULL)
            """, new { checklistId, touch }, transaction: tx, cancellationToken: ct));
        await RecountTicketAsync(conn, tx, ticketId, ct);
        return isComplete;
    }

    private static Task<int> RecountTicketAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid ticketId, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tickets t
               SET checklist_required_total = s.rt,
                   checklist_required_done  = s.rd
              FROM (SELECT COALESCE(SUM(required_total), 0)::int AS rt,
                           COALESCE(SUM(required_done), 0)::int  AS rd
                      FROM ticket_checklists WHERE ticket_id = @ticketId) s
             WHERE t.id = @ticketId
            """, new { ticketId }, transaction: tx, cancellationToken: ct));

    private sealed class StateRow
    {
        public Guid Id { get; set; }
        public Guid ChecklistId { get; set; }
        public Guid TicketId { get; set; }
        public string ChecklistName { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public bool IsComplete { get; set; }
    }

    private sealed class BlockerRow
    {
        public Guid ChecklistId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int OpenRequired { get; set; }
    }
}
