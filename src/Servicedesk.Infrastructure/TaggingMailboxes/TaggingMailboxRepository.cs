using Dapper;
using Npgsql;
using Servicedesk.Domain.TaggingMailboxes;

namespace Servicedesk.Infrastructure.TaggingMailboxes;

public sealed class TaggingMailboxRepository : ITaggingMailboxRepository
{
    private const string SelectColumns = """
        id          AS Id,
        name        AS Name,
        email       AS Email,
        is_active   AS IsActive,
        created_utc AS CreatedUtc,
        updated_utc AS UpdatedUtc
        """;

    private readonly NpgsqlDataSource _dataSource;

    public TaggingMailboxRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<TaggingMailbox>> ListAsync(CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM tagging_mailboxes ORDER BY is_active DESC, lower(name)";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<TaggingMailbox?> GetAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM tagging_mailboxes WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row is null ? null : MapToDomain(row);
    }

    public async Task<IReadOnlyList<TaggingMailbox>> SearchActiveAsync(string? search, int limit, CancellationToken ct)
    {
        var effectiveLimit = limit <= 0 ? 20 : Math.Min(limit, 50);
        var trimmed = (search ?? string.Empty).Trim();

        string sql;
        object args;
        if (trimmed.Length == 0)
        {
            sql = $"""
                SELECT {SelectColumns} FROM tagging_mailboxes
                WHERE is_active = TRUE
                ORDER BY lower(name)
                LIMIT @limit
                """;
            args = new { limit = effectiveLimit };
        }
        else
        {
            // ILIKE %term% on both name and email. Dapper parameterizes the
            // value; we escape '%' / '_' / '\' so a literal in the user's
            // input matches literally instead of acting as a wildcard.
            var escaped = trimmed
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            var pattern = "%" + escaped + "%";
            sql = $"""
                SELECT {SelectColumns} FROM tagging_mailboxes
                WHERE is_active = TRUE
                  AND (name ILIKE @pattern OR email ILIKE @pattern)
                ORDER BY lower(name)
                LIMIT @limit
                """;
            args = new { pattern, limit = effectiveLimit };
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<IReadOnlyList<TaggingMailbox>> ResolveActiveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids is null || ids.Count == 0) return Array.Empty<TaggingMailbox>();
        var distinct = ids.Distinct().ToArray();

        var sql = $"""
            SELECT {SelectColumns} FROM tagging_mailboxes
            WHERE is_active = TRUE AND id = ANY(@ids)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { ids = distinct }, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<Guid> CreateAsync(string name, string email, bool isActive, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO tagging_mailboxes (name, email, is_active)
            VALUES (@name, @email, @isActive)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql, new { name, email = email.ToLowerInvariant(), isActive }, cancellationToken: ct));
    }

    public async Task<bool> UpdateAsync(Guid id, string name, string email, bool isActive, CancellationToken ct)
    {
        const string sql = """
            UPDATE tagging_mailboxes
            SET name = @name, email = @email, is_active = @isActive, updated_utc = now()
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { id, name, email = email.ToLowerInvariant(), isActive }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM tagging_mailboxes WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    private static TaggingMailbox MapToDomain(Row r) =>
        new(r.Id, r.Name, r.Email, r.IsActive, r.CreatedUtc, r.UpdatedUtc);

    // Mutable class for Dapper column binding (see project memo on
    // positional-record-struct null bugs — avoided here).
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
