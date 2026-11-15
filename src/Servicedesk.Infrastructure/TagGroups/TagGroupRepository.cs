using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.TagGroups;

public sealed record TagGroupMember(Guid UserId, string Email);

/// <paramref name="PersonalAck"/> — every member acknowledges each tag for
/// themselves (e.g. an "Everyone" group) instead of one ack for the group.
public sealed record TagGroup(
    Guid Id, string Name, bool IsActive, bool PersonalAck, IReadOnlyList<TagGroupMember> Members,
    DateTime CreatedUtc, DateTime UpdatedUtc);

/// A group as the @@-picker and the mention flow see it.
public sealed record TagGroupRef(Guid Id, string Name, int MemberCount, bool PersonalAck);

public interface ITagGroupRepository
{
    Task<IReadOnlyList<TagGroup>> ListAsync(CancellationToken ct);
    Task<TagGroup?> GetAsync(Guid id, CancellationToken ct);
    /// Creates the group and sets its members in one transaction. Throws
    /// <see cref="PostgresException"/> 23505 on a duplicate active name.
    Task<Guid> CreateAsync(string name, bool isActive, bool personalAck, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);
    Task<bool> UpdateAsync(Guid id, string name, bool isActive, bool personalAck, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// Active groups for the @@-picker (name ILIKE, empty = first N).
    Task<IReadOnlyList<TagGroupRef>> SearchActiveAsync(string? search, int limit, CancellationToken ct);
    /// Of <paramref name="ids"/>, the active groups (unknown/inactive dropped).
    Task<IReadOnlyList<TagGroupRef>> ResolveActiveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    /// Current members (Agent/Admin accounts only) per group.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListMemberIdsAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct);
    /// Whether the user belongs to at least one active group (sidebar block).
    Task<bool> IsMemberOfAnyActiveAsync(Guid userId, CancellationToken ct);
}

/// v0.1.54 — Dapper repository for group tags' groups (admin config).
public sealed class TagGroupRepository : ITagGroupRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TagGroupRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<TagGroup>> ListAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var groups = (await conn.QueryAsync<GroupRow>(new CommandDefinition("""
            SELECT id AS Id, name AS Name, is_active AS IsActive, personal_ack AS PersonalAck, created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM tag_groups ORDER BY is_active DESC, lower(name), id
            """, cancellationToken: ct))).ToList();
        var members = await MembersAsync(conn, groups.Select(g => g.Id).ToArray(), ct);
        return groups.Select(g => ToGroup(g, members)).ToList();
    }

    public async Task<TagGroup?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var g = await conn.QuerySingleOrDefaultAsync<GroupRow>(new CommandDefinition("""
            SELECT id AS Id, name AS Name, is_active AS IsActive, personal_ack AS PersonalAck, created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM tag_groups WHERE id = @id
            """, new { id }, cancellationToken: ct));
        if (g is null) return null;
        return ToGroup(g, await MembersAsync(conn, new[] { id }, ct));
    }

    public async Task<Guid> CreateAsync(string name, bool isActive, bool personalAck, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "INSERT INTO tag_groups (name, is_active, personal_ack) VALUES (@name, @isActive, @personalAck) RETURNING id",
            new { name, isActive, personalAck }, tx, cancellationToken: ct));
        await SetMembersAsync(conn, tx, id, memberIds, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> UpdateAsync(Guid id, string name, bool isActive, bool personalAck, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tag_groups SET name = @name, is_active = @isActive, personal_ack = @personalAck, updated_utc = now()
            WHERE id = @id
            """, new { id, name, isActive, personalAck }, tx, cancellationToken: ct));
        if (n == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        await SetMembersAsync(conn, tx, id, memberIds, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // Members and the group's tags (incl. history) cascade.
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tag_groups WHERE id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<TagGroupRef>> SearchActiveAsync(string? search, int limit, CancellationToken ct)
    {
        var term = (search ?? string.Empty).Trim();
        if (term.Length > 100) term = term[..100];
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RefRow>(new CommandDefinition("""
            SELECT g.id AS Id, g.name AS Name, g.personal_ack AS PersonalAck,
                   (SELECT COUNT(*)::int FROM tag_group_members m WHERE m.group_id = g.id) AS MemberCount
            FROM tag_groups g
            WHERE g.is_active AND (@hasTerm = FALSE OR g.name ILIKE @like)
            ORDER BY lower(g.name), g.id
            LIMIT @limit
            """, new { hasTerm = term.Length > 0, like, limit = Math.Clamp(limit, 1, 50) }, cancellationToken: ct));
        return rows.Select(r => new TagGroupRef(r.Id, r.Name, r.MemberCount, r.PersonalAck)).ToList();
    }

    public async Task<IReadOnlyList<TagGroupRef>> ResolveActiveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<TagGroupRef>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RefRow>(new CommandDefinition("""
            SELECT g.id AS Id, g.name AS Name, g.personal_ack AS PersonalAck,
                   (SELECT COUNT(*)::int FROM tag_group_members m WHERE m.group_id = g.id) AS MemberCount
            FROM tag_groups g
            WHERE g.is_active AND g.id = ANY(@ids)
            """, new { ids = ids.Distinct().ToArray() }, cancellationToken: ct));
        return rows.Select(r => new TagGroupRef(r.Id, r.Name, r.MemberCount, r.PersonalAck)).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListMemberIdsAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct)
    {
        if (groupIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<Guid>>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid GroupId, Guid UserId)>(new CommandDefinition("""
            SELECT m.group_id, m.user_id
            FROM tag_group_members m
            JOIN users u ON u.id = m.user_id AND u.role_name IN ('Agent', 'Admin')
            WHERE m.group_id = ANY(@ids)
            """, new { ids = groupIds.Distinct().ToArray() }, cancellationToken: ct));
        return rows.GroupBy(r => r.GroupId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.UserId).ToList());
    }

    public async Task<bool> IsMemberOfAnyActiveAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM tag_group_members m
                JOIN tag_groups g ON g.id = m.group_id AND g.is_active
                WHERE m.user_id = @userId)
            """, new { userId }, cancellationToken: ct));
    }

    /// Replaces the member list. Only Agent/Admin accounts are kept — the
    /// endpoint validates too, this is the last line.
    private static async Task SetMembersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id,
        IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM tag_group_members WHERE group_id = @id AND user_id <> ALL(@ids)
            """, new { id, ids = memberIds.Distinct().ToArray() }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tag_group_members (group_id, user_id)
            SELECT @id, u.id FROM users u
            WHERE u.id = ANY(@ids) AND u.role_name IN ('Agent', 'Admin')
            ON CONFLICT DO NOTHING
            """, new { id, ids = memberIds.Distinct().ToArray() }, tx, cancellationToken: ct));
    }

    private static async Task<ILookup<Guid, TagGroupMember>> MembersAsync(NpgsqlConnection conn, Guid[] groupIds, CancellationToken ct)
    {
        if (groupIds.Length == 0) return Array.Empty<(Guid, TagGroupMember)>().ToLookup(x => x.Item1, x => x.Item2);
        var rows = await conn.QueryAsync<(Guid GroupId, Guid UserId, string Email)>(new CommandDefinition("""
            SELECT m.group_id, u.id, u.email::text
            FROM tag_group_members m JOIN users u ON u.id = m.user_id
            WHERE m.group_id = ANY(@groupIds)
            ORDER BY lower(u.email::text)
            """, new { groupIds }, cancellationToken: ct));
        return rows.ToLookup(r => r.GroupId, r => new TagGroupMember(r.UserId, r.Email));
    }

    private static TagGroup ToGroup(GroupRow g, ILookup<Guid, TagGroupMember> members)
        => new(g.Id, g.Name, g.IsActive, g.PersonalAck, members[g.Id].ToList(), g.CreatedUtc, g.UpdatedUtc);

    private sealed class GroupRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool PersonalAck { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class RefRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int MemberCount { get; set; }
        public bool PersonalAck { get; set; }
    }
}
