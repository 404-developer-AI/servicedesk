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

    public async Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, slug AS Slug, description AS Description,
                   color AS Color, icon AS Icon, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM queues
            ORDER BY sort_order, name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Queue>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, slug AS Slug, description AS Description,
                   color AS Color, icon AS Icon, sort_order AS SortOrder,
                   is_active AS IsActive, is_system AS IsSystem,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM queues WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Queue>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO queues (name, slug, description, color, icon, sort_order, is_active, is_system)
            VALUES (@Name, @Slug, @Description, @Color, @Icon, @SortOrder, @IsActive, @IsSystem)
            RETURNING id AS Id, name AS Name, slug AS Slug, description AS Description,
                      color AS Color, icon AS Icon, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Queue>(new CommandDefinition(sql, q, cancellationToken: ct));
    }

    public async Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string description, string color, string icon, int sortOrder, bool isActive, CancellationToken ct)
    {
        const string sql = """
            UPDATE queues SET name = @name, slug = @slug, description = @description,
                              color = @color, icon = @icon, sort_order = @sortOrder,
                              is_active = @isActive, updated_utc = now()
            WHERE id = @id
            RETURNING id AS Id, name AS Name, slug AS Slug, description AS Description,
                      color AS Color, icon AS Icon, sort_order AS SortOrder,
                      is_active AS IsActive, is_system AS IsSystem,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Queue>(new CommandDefinition(sql,
            new { id, name, slug, description, color, icon, sortOrder, isActive }, cancellationToken: ct));
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
}
