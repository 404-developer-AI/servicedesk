using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Security.IpBlocking;

public interface IIpBlockRepository
{
    /// Rules currently in force: every whitelist entry and every block that
    /// is permanent or not yet expired.
    Task<IReadOnlyList<IpRule>> ListActiveRulesAsync(CancellationToken ct);
    Task<IpRule?> GetRuleAsync(string ip, CancellationToken ct);
    Task UpsertRuleAsync(string ip, string kind, string source, string? reason, long? proposalId,
        string createdBy, DateTime? expiresUtc, CancellationToken ct);
    Task<bool> DeleteRuleAsync(string ip, CancellationToken ct);
    Task<int> DeleteExpiredBlocksAsync(CancellationToken ct);
    Task AddBlockedHitsAsync(IReadOnlyDictionary<string, (long Count, DateTime LastUtc)> hits, CancellationToken ct);

    /// Inserts the open proposal for this IP or refreshes it. Returns the id
    /// and whether it was newly created (only new proposals alert admins).
    Task<(long Id, bool Created)> UpsertOpenProposalAsync(IpThreatObservation observation,
        IReadOnlyList<string> reasons, string evidenceJson, bool knownLogin, bool autoBlocked, CancellationToken ct);
    Task<IReadOnlyList<IpProposal>> ListProposalsAsync(string? status, int limit, CancellationToken ct);
    Task<IpProposal?> GetProposalAsync(long id, CancellationToken ct);
    Task<int> CountOpenProposalsAsync(CancellationToken ct);
    /// Closes an open proposal; false when it was not open (already decided).
    Task<bool> DecideProposalAsync(long id, string status, string decidedBy, CancellationToken ct);
    /// Closes the open proposal for an IP, if any (used when an admin adds a
    /// rule by hand for an IP that also has a pending proposal).
    Task CloseOpenProposalForIpAsync(string ip, string status, string decidedBy, CancellationToken ct);

    /// Whether any successful staff or portal sign-in ever came from this IP.
    Task<int> CountSuccessfulLoginsAsync(string ip, CancellationToken ct);
}

public sealed class IpBlockRepository : IIpBlockRepository
{
    // Mirrors the partial index ix_audit_log_login_success_ip — keep in sync.
    internal static readonly string[] LoginSuccessEventTypes =
    {
        "login_success", "2fa_challenge_success", "auth.microsoft.login.success", "portal.login.success",
    };

    private const string RuleColumns = """
        ip          AS Ip,
        kind        AS Kind,
        source      AS Source,
        reason      AS Reason,
        proposal_id AS ProposalId,
        created_utc AS CreatedUtc,
        created_by  AS CreatedBy,
        expires_utc AS ExpiresUtc,
        hit_count   AS HitCount,
        last_hit_utc AS LastHitUtc
        """;

    private const string ProposalColumns = """
        id             AS Id,
        ip             AS Ip,
        status         AS Status,
        auto_blocked   AS AutoBlocked,
        reasons        AS Reasons,
        evidence::text AS EvidenceJson,
        known_login    AS KnownLogin,
        first_seen_utc AS FirstSeenUtc,
        last_seen_utc  AS LastSeenUtc,
        created_utc    AS CreatedUtc,
        decided_utc    AS DecidedUtc,
        decided_by     AS DecidedBy
        """;

    private readonly NpgsqlDataSource _dataSource;

    public IpBlockRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<IpRule>> ListActiveRulesAsync(CancellationToken ct)
    {
        var sql = $"""
            SELECT {RuleColumns}
              FROM security_ip_rules
             WHERE kind = 'whitelist' OR expires_utc IS NULL OR expires_utc > now()
             ORDER BY created_utc DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RuleRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IpRule?> GetRuleAsync(string ip, CancellationToken ct)
    {
        var sql = $"SELECT {RuleColumns} FROM security_ip_rules WHERE ip = @ip";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<RuleRow>(new CommandDefinition(sql, new { ip }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task UpsertRuleAsync(string ip, string kind, string source, string? reason, long? proposalId,
        string createdBy, DateTime? expiresUtc, CancellationToken ct)
    {
        // A rule change for an IP replaces the previous rule (block ⇄
        // whitelist, temp → permanent). Hit counters survive a block that is
        // merely confirmed, reset when the kind flips.
        const string sql = """
            INSERT INTO security_ip_rules (ip, kind, source, reason, proposal_id, created_by, expires_utc)
            VALUES (@ip, @kind, @source, @reason, @proposalId, @createdBy, @expiresUtc)
            ON CONFLICT (ip) DO UPDATE SET
                hit_count    = CASE WHEN security_ip_rules.kind = EXCLUDED.kind THEN security_ip_rules.hit_count ELSE 0 END,
                last_hit_utc = CASE WHEN security_ip_rules.kind = EXCLUDED.kind THEN security_ip_rules.last_hit_utc ELSE NULL END,
                kind         = EXCLUDED.kind,
                source       = EXCLUDED.source,
                reason       = EXCLUDED.reason,
                proposal_id  = COALESCE(EXCLUDED.proposal_id, security_ip_rules.proposal_id),
                created_utc  = now(),
                created_by   = EXCLUDED.created_by,
                expires_utc  = EXCLUDED.expires_utc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { ip, kind, source, reason, proposalId, createdBy, expiresUtc }, cancellationToken: ct));
    }

    public async Task<bool> DeleteRuleAsync(string ip, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM security_ip_rules WHERE ip = @ip", new { ip }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<int> DeleteExpiredBlocksAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM security_ip_rules WHERE kind = 'block' AND expires_utc IS NOT NULL AND expires_utc <= now()",
            cancellationToken: ct));
    }

    public async Task AddBlockedHitsAsync(IReadOnlyDictionary<string, (long Count, DateTime LastUtc)> hits, CancellationToken ct)
    {
        if (hits.Count == 0) return;
        const string sql = """
            UPDATE security_ip_rules r
               SET hit_count    = r.hit_count + h.cnt,
                   last_hit_utc = GREATEST(COALESCE(r.last_hit_utc, h.last_utc), h.last_utc)
              FROM unnest(@ips, @counts, @lasts) AS h(ip, cnt, last_utc)
             WHERE r.ip = h.ip
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            ips = hits.Keys.ToArray(),
            counts = hits.Values.Select(v => v.Count).ToArray(),
            lasts = hits.Values.Select(v => DateTime.SpecifyKind(v.LastUtc, DateTimeKind.Utc)).ToArray(),
        }, cancellationToken: ct));
    }

    public async Task<(long Id, bool Created)> UpsertOpenProposalAsync(IpThreatObservation observation,
        IReadOnlyList<string> reasons, string evidenceJson, bool knownLogin, bool autoBlocked, CancellationToken ct)
    {
        // One open proposal per IP (partial unique index). A refresh keeps the
        // original first_seen, merges reasons and replaces the evidence with
        // the latest window. `xmax = 0` is true only for a freshly inserted
        // row — the standard Postgres idiom for "inserted vs. updated".
        const string sql = """
            INSERT INTO security_ip_proposals
                (ip, status, auto_blocked, reasons, evidence, known_login, first_seen_utc, last_seen_utc)
            VALUES
                (@ip, 'open', @autoBlocked, @reasons, CAST(@evidence AS jsonb), @knownLogin, @firstSeen, @lastSeen)
            ON CONFLICT (ip) WHERE status = 'open' DO UPDATE SET
                auto_blocked  = security_ip_proposals.auto_blocked OR EXCLUDED.auto_blocked,
                reasons       = ARRAY(SELECT DISTINCT unnest(security_ip_proposals.reasons || EXCLUDED.reasons) ORDER BY 1),
                evidence      = EXCLUDED.evidence,
                known_login   = EXCLUDED.known_login,
                last_seen_utc = GREATEST(security_ip_proposals.last_seen_utc, EXCLUDED.last_seen_utc)
            RETURNING id AS Id, (xmax = 0) AS Created
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<UpsertRow>(new CommandDefinition(sql, new
        {
            ip = observation.Ip,
            autoBlocked,
            reasons = reasons.ToArray(),
            evidence = evidenceJson,
            knownLogin,
            firstSeen = DateTime.SpecifyKind(observation.FirstSeenUtc, DateTimeKind.Utc),
            lastSeen = DateTime.SpecifyKind(observation.LastSeenUtc, DateTimeKind.Utc),
        }, cancellationToken: ct));
        return (row.Id, row.Created);
    }

    public async Task<IReadOnlyList<IpProposal>> ListProposalsAsync(string? status, int limit, CancellationToken ct)
    {
        var sql = $"""
            SELECT {ProposalColumns}
              FROM security_ip_proposals
             WHERE (@status::text IS NULL OR status = @status)
             ORDER BY created_utc DESC, id DESC
             LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ProposalRow>(new CommandDefinition(sql,
            new { status, limit = Math.Clamp(limit, 1, 500) }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IpProposal?> GetProposalAsync(long id, CancellationToken ct)
    {
        var sql = $"SELECT {ProposalColumns} FROM security_ip_proposals WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<ProposalRow>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<int> CountOpenProposalsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM security_ip_proposals WHERE status = 'open'", cancellationToken: ct));
    }

    public async Task<bool> DecideProposalAsync(long id, string status, string decidedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE security_ip_proposals
               SET status = @status, decided_utc = now(), decided_by = @decidedBy
             WHERE id = @id AND status = 'open'
            """, new { id, status, decidedBy }, cancellationToken: ct));
        return n > 0;
    }

    public async Task CloseOpenProposalForIpAsync(string ip, string status, string decidedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE security_ip_proposals
               SET status = @status, decided_utc = now(), decided_by = @decidedBy
             WHERE ip = @ip AND status = 'open'
            """, new { ip, status, decidedBy }, cancellationToken: ct));
    }

    public async Task<int> CountSuccessfulLoginsAsync(string ip, CancellationToken ct)
    {
        // audit_log.client_ip is the raw RemoteIpAddress text; match both the
        // normalised and the IPv4-mapped spelling. The event_type predicate
        // must stay literal-compatible with the partial index.
        const string sql = """
            SELECT count(*)::int
              FROM audit_log
             WHERE event_type IN ('login_success','2fa_challenge_success','auth.microsoft.login.success','portal.login.success')
               AND client_ip IN (@ip, @mapped)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { ip, mapped = "::ffff:" + ip }, cancellationToken: ct));
    }

    private sealed class RuleRow
    {
        public string Ip { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public long? ProposalId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime? ExpiresUtc { get; set; }
        public long HitCount { get; set; }
        public DateTime? LastHitUtc { get; set; }

        public IpRule ToModel() => new(Ip, Kind, Source, Reason, ProposalId,
            DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc), CreatedBy,
            ExpiresUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null,
            HitCount,
            LastHitUtc is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : null);
    }

    private sealed class ProposalRow
    {
        public long Id { get; set; }
        public string Ip { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool AutoBlocked { get; set; }
        public string[] Reasons { get; set; } = Array.Empty<string>();
        public string EvidenceJson { get; set; } = "{}";
        public bool KnownLogin { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? DecidedUtc { get; set; }
        public string? DecidedBy { get; set; }

        public IpProposal ToModel() => new(Id, Ip, Status, AutoBlocked, Reasons, EvidenceJson, KnownLogin,
            DateTime.SpecifyKind(FirstSeenUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(LastSeenUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc),
            DecidedUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
            DecidedBy);
    }

    private sealed class UpsertRow
    {
        public long Id { get; set; }
        public bool Created { get; set; }
    }
}
