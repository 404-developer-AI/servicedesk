using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Contracts.Reports;

/// One row to append to the per-company send history. Time-sensitive values are
/// server-stamped (sent_utc defaults to now()); summary counts are a snapshot of
/// protection at send time so the history needn't re-derive it.
public sealed class M365ReportSendEntry
{
    public Guid CompanyId { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public Guid? SentBy { get; set; }
    public string? SentByName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public IReadOnlyList<ReportRecipient> Recipients { get; set; } = Array.Empty<ReportRecipient>();
    public IReadOnlyList<string> Columns { get; set; } = Array.Empty<string>();
    public string Scope { get; set; } = "all";
    public int MailboxCount { get; set; }
    public int? SpamProtected { get; set; }
    public int? ExchangeProtected { get; set; }
    public int? OneDriveProtected { get; set; }
    public string? InternetMessageId { get; set; }
    public string Status { get; set; } = "sent";
    public string? Error { get; set; }
}

/// "Last sent" summary for the matching list / company header.
public sealed class M365ReportLastSent
{
    public Guid CompanyId { get; set; }
    public DateTime SentUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? SentByName { get; set; }
    public string? Subject { get; set; }
    public int MailboxCount { get; set; }
}

public interface IM365ReportSendLog
{
    Task<Guid> InsertAsync(M365ReportSendEntry entry, CancellationToken ct);

    /// Latest send per company, keyed by company id, for the matching list.
    Task<IReadOnlyDictionary<Guid, M365ReportLastSent>> GetLastSentMapAsync(CancellationToken ct);

    /// Latest send for one company (company header / detail).
    Task<M365ReportLastSent?> GetLastSentAsync(Guid companyId, CancellationToken ct);
}

public sealed class M365ReportSendLog : IM365ReportSendLog
{
    private readonly NpgsqlDataSource _dataSource;

    public M365ReportSendLog(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Guid> InsertAsync(M365ReportSendEntry e, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO m365_report_sends
                (company_id, template_id, template_name, sent_by, sent_by_name, subject,
                 recipients, columns, scope, mailbox_count,
                 spam_protected, exchange_protected, onedrive_protected,
                 internet_message_id, status, error)
            VALUES
                (@companyId, @templateId, @templateName, @sentBy, @sentByName, @subject,
                 @recipients::jsonb, @columns, @scope, @mailboxCount,
                 @spamProtected, @exchangeProtected, @onedriveProtected,
                 @internetMessageId, @status, @error)
            RETURNING id
            """;

        var recipientsJson = JsonSerializer.Serialize(
            e.Recipients.Select(r => new { address = r.Address, name = r.Name }));

        var p = new DynamicParameters();
        p.Add("companyId", e.CompanyId);
        p.Add("templateId", e.TemplateId);
        p.Add("templateName", e.TemplateName);
        p.Add("sentBy", e.SentBy);
        p.Add("sentByName", e.SentByName);
        p.Add("subject", e.Subject);
        p.Add("recipients", recipientsJson, dbType: DbType.String);
        p.Add("columns", e.Columns.ToArray());
        p.Add("scope", e.Scope);
        p.Add("mailboxCount", e.MailboxCount);
        p.Add("spamProtected", e.SpamProtected);
        p.Add("exchangeProtected", e.ExchangeProtected);
        p.Add("onedriveProtected", e.OneDriveProtected);
        p.Add("internetMessageId", e.InternetMessageId);
        p.Add("status", e.Status);
        p.Add("error", e.Error);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, p, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<Guid, M365ReportLastSent>> GetLastSentMapAsync(CancellationToken ct)
    {
        // DISTINCT ON keeps only the newest row per company.
        const string sql = """
            SELECT DISTINCT ON (company_id)
                   company_id    AS CompanyId,
                   sent_utc      AS SentUtc,
                   status        AS Status,
                   sent_by_name  AS SentByName,
                   subject       AS Subject,
                   mailbox_count AS MailboxCount
              FROM m365_report_sends
             ORDER BY company_id, sent_utc DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<M365ReportLastSent>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToDictionary(r => r.CompanyId);
    }

    public async Task<M365ReportLastSent?> GetLastSentAsync(Guid companyId, CancellationToken ct)
    {
        const string sql = """
            SELECT company_id    AS CompanyId,
                   sent_utc      AS SentUtc,
                   status        AS Status,
                   sent_by_name  AS SentByName,
                   subject       AS Subject,
                   mailbox_count AS MailboxCount
              FROM m365_report_sends
             WHERE company_id = @companyId
             ORDER BY sent_utc DESC
             LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<M365ReportLastSent>(new CommandDefinition(sql, new { companyId }, cancellationToken: ct));
    }
}
