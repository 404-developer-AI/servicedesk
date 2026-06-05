using Dapper;
using Npgsql;
using Servicedesk.Domain.Taxonomy;

namespace Servicedesk.Infrastructure.Persistence.Taxonomy;

/// Dapper-backed CRUD for the four taxonomy tables (queues, priorities,
/// statuses, categories). Admin-only — authorization is enforced at the API
/// layer. System-row protection (no deleting <c>is_system</c> rows, renaming
/// allowed) is enforced here so the invariant can't be bypassed.
public sealed class TaxonomyRepository : ITaxonomyRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TaxonomyRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // ---------- Queues ----------
    //
    // All four queue queries hydrate into the private QueueRow class
    // (property-setter binding) and then project to the Queue record via
    // MapQueueRow. This mirrors the proven ComposeTemplateRepository
    // pattern: Dapper's positional-record materializer cannot reconcile
    // an Npgsql uuid[] column (`System.Array`) with a constructor param
    // of type Guid[]? or IReadOnlyList<Guid>?, so we go around that by
    // never querying directly into the record. The Row class accepts the
    // array via a plain `Guid[]?` setter, which Dapper hydrates cleanly.

    private const string QueueSelectColumns = """
        id AS Id, name AS Name, slug AS Slug, description AS Description,
        color AS Color, icon AS Icon, sort_order AS SortOrder,
        is_active AS IsActive, is_system AS IsSystem,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc,
        inbound_mailbox_address AS InboundMailboxAddress,
        outbound_mailbox_address AS OutboundMailboxAddress,
        inbound_folder_id AS InboundFolderId,
        inbound_folder_name AS InboundFolderName,
        allowed_status_ids AS AllowedStatusIds,
        default_status_id AS DefaultStatusId,
        inbound_polling_enabled AS InboundPollingEnabled
        """;

    private static Queue MapQueueRow(QueueRow r) => new(
        r.Id, r.Name, r.Slug, r.Description, r.Color, r.Icon,
        r.SortOrder, r.IsActive, r.IsSystem, r.CreatedUtc, r.UpdatedUtc,
        r.InboundMailboxAddress, r.OutboundMailboxAddress,
        r.InboundFolderId, r.InboundFolderName,
        r.AllowedStatusIds ?? Array.Empty<Guid>(),
        r.DefaultStatusId,
        r.InboundPollingEnabled);

    private sealed class QueueRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool IsActive { get; set; }
        public bool IsSystem { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string? InboundMailboxAddress { get; set; }
        public string? OutboundMailboxAddress { get; set; }
        public string? InboundFolderId { get; set; }
        public string? InboundFolderName { get; set; }
        public Guid[]? AllowedStatusIds { get; set; }
        public Guid? DefaultStatusId { get; set; }
        public bool InboundPollingEnabled { get; set; } = true;
    }

    public async Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct)
    {
        var sql = $"SELECT {QueueSelectColumns} FROM queues ORDER BY sort_order, name";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<QueueRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapQueueRow).ToList();
    }

    public async Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {QueueSelectColumns} FROM queues WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<QueueRow>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row is null ? null : MapQueueRow(row);
    }

    public async Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct)
    {
        var sql = $$"""
            INSERT INTO queues (name, slug, description, color, icon, sort_order, is_active, is_system,
                                inbound_mailbox_address, outbound_mailbox_address,
                                inbound_folder_id, inbound_folder_name,
                                allowed_status_ids, default_status_id)
            VALUES (@Name, @Slug, @Description, @Color, @Icon, @SortOrder, @IsActive, @IsSystem,
                    @InboundMailboxAddress, @OutboundMailboxAddress,
                    @InboundFolderId, @InboundFolderName,
                    COALESCE(@AllowedStatusIds, '{}'::uuid[]), @DefaultStatusId)
            RETURNING {{QueueSelectColumns}}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<QueueRow>(new CommandDefinition(sql, new
        {
            q.Name, q.Slug, q.Description, q.Color, q.Icon, q.SortOrder,
            q.IsActive, q.IsSystem,
            q.InboundMailboxAddress, q.OutboundMailboxAddress,
            q.InboundFolderId, q.InboundFolderName,
            AllowedStatusIds = (q.AllowedStatusIds ?? Array.Empty<Guid>()).ToArray(),
            q.DefaultStatusId,
        }, cancellationToken: ct));
        return MapQueueRow(row);
    }

    public async Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string description, string color, string icon, int sortOrder, bool isActive, string? inboundMailboxAddress, string? outboundMailboxAddress, string? inboundFolderId, string? inboundFolderName, IReadOnlyList<Guid> allowedStatusIds, Guid? defaultStatusId, CancellationToken ct)
    {
        // A Microsoft Graph delta token is bound to the folder it was issued
        // for. When the admin re-points a queue at a different inbound mailbox
        // or folder, the stale delta_link in mail_poll_state would otherwise be
        // re-used against the new folder — Graph then reports "no changes" and
        // the poller silently returns 0 forever. So we wipe the cursor (and the
        // failure backoff) in the same statement whenever mailbox/folder
        // actually changes, forcing the next poll to do a full re-read. `prev`
        // reads the pre-update values from the same snapshot; the data-modifying
        // `reset` CTE always executes even though the final SELECT ignores it.
        var sql = $$"""
            WITH prev AS (
                SELECT inbound_mailbox_address AS old_mailbox,
                       inbound_folder_id       AS old_folder
                FROM queues WHERE id = @id
            ),
            upd AS (
                UPDATE queues SET name = @name, slug = @slug, description = @description,
                                  color = @color, icon = @icon, sort_order = @sortOrder,
                                  is_active = @isActive,
                                  inbound_mailbox_address = @inboundMailboxAddress,
                                  outbound_mailbox_address = @outboundMailboxAddress,
                                  inbound_folder_id = @inboundFolderId,
                                  inbound_folder_name = @inboundFolderName,
                                  allowed_status_ids = COALESCE(@allowedStatusIds, '{}'::uuid[]),
                                  default_status_id = @defaultStatusId,
                                  updated_utc = now()
                WHERE id = @id
                RETURNING {{QueueSelectColumns}}
            ),
            reset AS (
                UPDATE mail_poll_state s
                SET delta_link = NULL, consecutive_failures = 0,
                    last_error = NULL, updated_utc = now()
                FROM prev
                WHERE s.queue_id = @id
                  AND (prev.old_folder  IS DISTINCT FROM @inboundFolderId
                    OR prev.old_mailbox IS DISTINCT FROM @inboundMailboxAddress)
            )
            SELECT * FROM upd
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<QueueRow>(new CommandDefinition(sql,
            new { id, name, slug, description, color, icon, sortOrder, isActive,
                  inboundMailboxAddress, outboundMailboxAddress,
                  inboundFolderId, inboundFolderName,
                  allowedStatusIds = allowedStatusIds.ToArray(),
                  defaultStatusId }, cancellationToken: ct));
        return row is null ? null : MapQueueRow(row);
    }

    public async Task<bool> SetQueueInboundPollingAsync(Guid id, bool enabled, CancellationToken ct)
    {
        // Targeted single-column update — does NOT touch updated_utc on purpose,
        // since pausing/resuming polling isn't a content edit of the queue.
        const string sql = "UPDATE queues SET inbound_polling_enabled = @enabled WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, enabled }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<DeleteResult> DeleteQueueAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(Guid? Id, int TicketCount)>(new CommandDefinition("""
            SELECT q.id AS Id,
                   (SELECT count(*) FROM tickets t WHERE t.queue_id = q.id AND t.is_deleted = FALSE)::int AS TicketCount
            FROM queues q WHERE q.id = @id
            """, new { id }, cancellationToken: ct));
        if (row.Id is null) return DeleteResult.NotFound;
        if (row.TicketCount > 0) return DeleteResult.InUse;
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM queues WHERE id = @id", new { id }, cancellationToken: ct));
        return DeleteResult.Deleted;
    }

    // ---------- Priorities ----------

    public async Task<IReadOnlyList<Priority>> ListPrioritiesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, slug AS Slug, level AS Level,
                   color AS Color, icon AS Icon, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM priorities
            ORDER BY sort_order, level, name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Priority>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<Priority?> GetPriorityAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, slug AS Slug, level AS Level,
                   color AS Color, icon AS Icon, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM priorities WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Priority>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Priority> CreatePriorityAsync(Priority p, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (p.IsDefault)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE priorities SET is_default = FALSE WHERE is_default = TRUE",
                transaction: tx, cancellationToken: ct));
        }

        const string sql = """
            INSERT INTO priorities (name, slug, level, color, icon, sort_order, is_active, is_system, is_default)
            VALUES (@Name, @Slug, @Level, @Color, @Icon, @SortOrder, @IsActive, @IsSystem, @IsDefault)
            RETURNING id AS Id, name AS Name, slug AS Slug, level AS Level,
                      color AS Color, icon AS Icon, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        var created = await conn.QuerySingleAsync<Priority>(new CommandDefinition(sql, p, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return created;
    }

    public async Task<Priority?> UpdatePriorityAsync(Guid id, string name, string slug, int level, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (isDefault)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE priorities SET is_default = FALSE WHERE is_default = TRUE AND id <> @id",
                new { id }, tx, cancellationToken: ct));
        }

        const string sql = """
            UPDATE priorities SET name = @name, slug = @slug, level = @level,
                                  color = @color, icon = @icon, sort_order = @sortOrder,
                                  is_active = @isActive, is_default = @isDefault, updated_utc = now()
            WHERE id = @id
            RETURNING id AS Id, name AS Name, slug AS Slug, level AS Level,
                      color AS Color, icon AS Icon, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        var updated = await conn.QueryFirstOrDefaultAsync<Priority>(new CommandDefinition(sql,
            new { id, name, slug, level, color, icon, sortOrder, isActive, isDefault },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<DeleteResult> DeletePriorityAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(Guid? Id, bool IsDefault, int TicketCount)>(new CommandDefinition("""
            SELECT p.id AS Id, p.is_default AS IsDefault,
                   (SELECT count(*) FROM tickets t WHERE t.priority_id = p.id AND t.is_deleted = FALSE)::int AS TicketCount
            FROM priorities p WHERE p.id = @id
            """, new { id }, cancellationToken: ct));
        if (row.Id is null) return DeleteResult.NotFound;
        if (row.IsDefault) return DeleteResult.SystemProtected;
        if (row.TicketCount > 0) return DeleteResult.InUse;
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM priorities WHERE id = @id", new { id }, cancellationToken: ct));
        return DeleteResult.Deleted;
    }

    // ---------- Statuses ----------

    public async Task<IReadOnlyList<Status>> ListStatusesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, slug AS Slug, state_category AS StateCategory,
                   color AS Color, icon AS Icon, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM statuses
            ORDER BY sort_order, name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Status>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<Status?> GetStatusAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, slug AS Slug, state_category AS StateCategory,
                   color AS Color, icon AS Icon, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM statuses WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Status>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Status> CreateStatusAsync(Status s, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO statuses (name, slug, state_category, color, icon, sort_order, is_active, is_system, is_default)
            VALUES (@Name, @Slug, @StateCategory, @Color, @Icon, @SortOrder, @IsActive, @IsSystem, @IsDefault)
            RETURNING id AS Id, name AS Name, slug AS Slug, state_category AS StateCategory,
                      color AS Color, icon AS Icon, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Status>(new CommandDefinition(sql, s, cancellationToken: ct));
    }

    public async Task<Status?> UpdateStatusAsync(Guid id, string name, string slug, string stateCategory, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Only one row can be is_default = true at a time — enforce it here
        // so the API doesn't need to do a two-step update.
        if (isDefault)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE statuses SET is_default = FALSE WHERE is_default = TRUE AND id <> @id",
                new { id }, tx, cancellationToken: ct));
        }

        const string sql = """
            UPDATE statuses SET name = @name, slug = @slug, state_category = @stateCategory,
                                color = @color, icon = @icon, sort_order = @sortOrder,
                                is_active = @isActive, is_default = @isDefault, updated_utc = now()
            WHERE id = @id
            RETURNING id AS Id, name AS Name, slug AS Slug, state_category AS StateCategory,
                      color AS Color, icon AS Icon, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem, is_default AS IsDefault,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        var updated = await conn.QueryFirstOrDefaultAsync<Status>(new CommandDefinition(sql,
            new { id, name, slug, stateCategory, color, icon, sortOrder, isActive, isDefault },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<DeleteResult> DeleteStatusAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(Guid? Id, bool IsSystem, int TicketCount)>(new CommandDefinition("""
            SELECT s.id AS Id, s.is_system AS IsSystem,
                   (SELECT count(*) FROM tickets t WHERE t.status_id = s.id AND t.is_deleted = FALSE)::int AS TicketCount
            FROM statuses s WHERE s.id = @id
            """, new { id }, cancellationToken: ct));
        if (row.Id is null) return DeleteResult.NotFound;
        if (row.IsSystem) return DeleteResult.SystemProtected;
        if (row.TicketCount > 0) return DeleteResult.InUse;
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM statuses WHERE id = @id", new { id }, cancellationToken: ct));
        return DeleteResult.Deleted;
    }

    // ---------- Categories ----------

    public async Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, parent_id AS ParentId, name AS Name, slug AS Slug,
                   description AS Description, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM categories
            ORDER BY sort_order, name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Category>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<Category?> GetCategoryAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, parent_id AS ParentId, name AS Name, slug AS Slug,
                   description AS Description, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM categories WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Category>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Category> CreateCategoryAsync(Category c, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO categories (parent_id, name, slug, description, sort_order, is_active, is_system)
            VALUES (@ParentId, @Name, @Slug, @Description, @SortOrder, @IsActive, @IsSystem)
            RETURNING id AS Id, parent_id AS ParentId, name AS Name, slug AS Slug,
                      description AS Description, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Category>(new CommandDefinition(sql, c, cancellationToken: ct));
    }

    public async Task<Category?> UpdateCategoryAsync(Guid id, Guid? parentId, string name, string slug, string description, int sortOrder, bool isActive, CancellationToken ct)
    {
        const string sql = """
            UPDATE categories SET parent_id = @parentId, name = @name, slug = @slug,
                                  description = @description, sort_order = @sortOrder,
                                  is_active = @isActive, updated_utc = now()
            WHERE id = @id
            RETURNING id AS Id, parent_id AS ParentId, name AS Name, slug AS Slug,
                      description AS Description, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Category>(new CommandDefinition(sql,
            new { id, parentId, name, slug, description, sortOrder, isActive }, cancellationToken: ct));
    }

    public async Task<DeleteResult> DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(Guid? Id, bool IsSystem, int TicketCount, int ChildCount)>(new CommandDefinition("""
            SELECT c.id AS Id, c.is_system AS IsSystem,
                   (SELECT count(*) FROM tickets t WHERE t.category_id = c.id AND t.is_deleted = FALSE)::int AS TicketCount,
                   (SELECT count(*) FROM categories ch WHERE ch.parent_id = c.id)::int AS ChildCount
            FROM categories c WHERE c.id = @id
            """, new { id }, cancellationToken: ct));
        if (row.Id is null) return DeleteResult.NotFound;
        if (row.IsSystem) return DeleteResult.SystemProtected;
        if (row.TicketCount > 0 || row.ChildCount > 0) return DeleteResult.InUse;
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM categories WHERE id = @id", new { id }, cancellationToken: ct));
        return DeleteResult.Deleted;
    }

    // ---------- Ticket types (v0.0.39) ----------

    private const string TicketTypeColumns = """
        id AS Id, code AS Code, label AS Label, description AS Description,
        icon AS Icon, color AS Color, sort_order AS SortOrder,
        is_active AS IsActive, is_system AS IsSystem,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
        """;

    public async Task<IReadOnlyList<TicketType>> ListTicketTypesAsync(CancellationToken ct)
    {
        const string sql = $"""
            SELECT {TicketTypeColumns}
            FROM ticket_types
            ORDER BY sort_order, lower(label)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TicketType>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<TicketType?> GetTicketTypeAsync(Guid id, CancellationToken ct)
    {
        const string sql = $"""
            SELECT {TicketTypeColumns}
            FROM ticket_types WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketType>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<TicketType?> GetTicketTypeByCodeAsync(string code, CancellationToken ct)
    {
        // code column is CITEXT — case-insensitive equality is automatic.
        const string sql = $"""
            SELECT {TicketTypeColumns}
            FROM ticket_types WHERE code = @code
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketType>(new CommandDefinition(sql, new { code }, cancellationToken: ct));
    }

    public async Task<TicketType> CreateTicketTypeAsync(TicketType t, CancellationToken ct)
    {
        const string sql = $"""
            INSERT INTO ticket_types (code, label, description, icon, color, sort_order, is_active, is_system)
            VALUES (@Code, @Label, @Description, @Icon, @Color, @SortOrder, @IsActive, @IsSystem)
            RETURNING {TicketTypeColumns}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<TicketType>(new CommandDefinition(sql, t, cancellationToken: ct));
    }

    public async Task<TicketType?> UpdateTicketTypeAsync(Guid id, string code, string label, string description, string icon, string color, int sortOrder, bool isActive, CancellationToken ct)
    {
        // is_system stays as-is on update; the seeded 'support' row keeps
        // its protection flag regardless of how the admin tweaks the
        // label/icon/color so deletes remain blocked.
        const string sql = $"""
            UPDATE ticket_types SET
                code = @code, label = @label, description = @description,
                icon = @icon, color = @color, sort_order = @sortOrder,
                is_active = @isActive, updated_utc = now()
            WHERE id = @id
            RETURNING {TicketTypeColumns}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketType>(new CommandDefinition(sql,
            new { id, code, label, description, icon, color, sortOrder, isActive }, cancellationToken: ct));
    }

    public async Task<DeleteResult> DeleteTicketTypeAsync(Guid id, CancellationToken ct)
    {
        // A type is deletable when no live tickets reference it AND no
        // active manual trigger is bound to it. System rows (the seeded
        // 'support') stay protected regardless of usage to keep the
        // default-fallback path safe.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(Guid? Id, bool IsSystem, int TicketCount, int TriggerCount)>(new CommandDefinition("""
            SELECT tt.id AS Id, tt.is_system AS IsSystem,
                   (SELECT count(*) FROM tickets t  WHERE t.ticket_type_id = tt.id AND t.is_deleted = FALSE)::int AS TicketCount,
                   (SELECT count(*) FROM triggers tr WHERE tr.manual_ticket_type_id = tt.id)::int AS TriggerCount
            FROM ticket_types tt WHERE tt.id = @id
            """, new { id }, cancellationToken: ct));
        if (row.Id is null) return DeleteResult.NotFound;
        if (row.IsSystem) return DeleteResult.SystemProtected;
        if (row.TicketCount > 0 || row.TriggerCount > 0) return DeleteResult.InUse;
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM ticket_types WHERE id = @id", new { id }, cancellationToken: ct));
        return DeleteResult.Deleted;
    }
}
