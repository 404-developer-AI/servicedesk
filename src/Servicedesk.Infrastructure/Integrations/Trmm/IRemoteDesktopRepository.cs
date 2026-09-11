namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Read model + note CRUD for Assets → Remote Desktop (v0.1.10).
/// Dapper-backed. The list is deliberately unpaginated: it is scoped to
/// server agents (a few hundred at most per install), so grouping per
/// client happens in memory on the API side.
public interface IRemoteDesktopRepository
{
    /// Every server agent (plus any non-server agent that has an RDS
    /// row) joined with its RDS result, client, company and site. The
    /// optional search narrows on hostname, client name/code and
    /// company name. Rows with <see cref="RemoteDesktopRow.Status"/> null
    /// have never been visited by the RDS sync.
    Task<IReadOnlyList<RemoteDesktopRow>> ListAsync(string? search, CancellationToken ct);

    /// Per-client note counts for the badges on the client headers.
    Task<IReadOnlyList<ClientNoteSummary>> ListNoteSummariesAsync(CancellationToken ct);

    Task<RemoteDesktopSyncState> GetRdsSyncStateAsync(CancellationToken ct);

    Task<bool> ClientExistsAsync(long trmmClientId, CancellationToken ct);
    Task<IReadOnlyList<ClientNote>> ListNotesAsync(long trmmClientId, CancellationToken ct);
    Task<ClientNote?> GetNoteAsync(Guid id, CancellationToken ct);
    Task<ClientNote> AddNoteAsync(long trmmClientId, Guid authorId, string body, CancellationToken ct);
    Task<ClientNote?> UpdateNoteAsync(Guid id, string body, CancellationToken ct);
    Task<bool> DeleteNoteAsync(Guid id, CancellationToken ct);
}

public sealed class RemoteDesktopRow
{
    public Guid Id { get; set; }
    public string TrmmAgentId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string AgentType { get; set; } = "";
    public string? OsName { get; set; }
    public string? OsFamily { get; set; }
    public string? OsBuild { get; set; }
    public bool Online { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string SiteName { get; set; } = "";
    public long TrmmClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string? ClientCode { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    /// One of <see cref="RdsStatus"/>, or null when the RDS sync has not
    /// visited this agent yet.
    public string? Status { get; set; }
    public string? CheckStatus { get; set; }
    public long? Retcode { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public DateTime? LastLoginLocal { get; set; }
    public string? LastLoginKind { get; set; }
    public string? LastLoginUser { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? FetchedUtc { get; set; }
    public string? FetchError { get; set; }
}

public sealed class ClientNoteSummary
{
    public long TrmmClientId { get; set; }
    public int NoteCount { get; set; }
    public DateTime? LastNoteUtc { get; set; }
}

public sealed class ClientNote
{
    public Guid Id { get; set; }
    public long TrmmClientId { get; set; }
    public string Body { get; set; } = "";
    public Guid? AuthorId { get; set; }
    public string? AuthorEmail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RemoteDesktopSyncState
{
    public DateTime? LastRdsSyncUtc { get; set; }
    public string? LastRdsStatus { get; set; }
    public string? LastRdsError { get; set; }
}
