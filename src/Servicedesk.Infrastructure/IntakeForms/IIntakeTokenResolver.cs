using Dapper;
using Npgsql;
using Servicedesk.Domain.IntakeForms;

namespace Servicedesk.Infrastructure.IntakeForms;

/// Resolves the supported <see cref="IntakeTokens"/> against the ticket +
/// requester contact + company so a template's default-token binding turns
/// into a concrete prefill string at the moment the form instance is
/// created. Unknown or unresolvable tokens collapse to empty string — the
/// customer sees an empty input rather than the raw token.
///
/// A single-query implementation keeps the agent-side create path cheap:
/// one LEFT JOIN returns every field we need for one ticket.
public interface IIntakeTokenResolver
{
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(Guid ticketId, CancellationToken ct);
}

public sealed class IntakeTokenResolver : IIntakeTokenResolver
{
    private readonly NpgsqlDataSource _dataSource;

    public IntakeTokenResolver(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(Guid ticketId, CancellationToken ct)
    {
        const string sql = """
            SELECT t.number                                      AS TicketNumber,
                   t.subject                                     AS TicketSubject,
                   cat.name                                      AS CategoryName,
                   c.email::text                                 AS RequesterEmail,
                   NULLIF(TRIM(c.first_name || ' ' || c.last_name), '') AS RequesterName,
                   co.name                                       AS CompanyName
            FROM tickets t
            LEFT JOIN contacts c      ON c.id = t.requester_contact_id
            LEFT JOIN companies co    ON co.id = t.company_id
            LEFT JOIN categories cat  ON cat.id = t.category_id
            WHERE t.id = @ticketId
            LIMIT 1
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<ContextRow?>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (row is null)
        {
            // Seed every supported token with empty string so template
            // designers can bind any token without worrying about the
            // map missing a key on resolve.
            foreach (var t in IntakeTokens.Supported) dict[t] = string.Empty;
            return dict;
        }

        dict[IntakeTokens.RequesterName] = row.Value.RequesterName ?? string.Empty;
        dict[IntakeTokens.RequesterEmail] = row.Value.RequesterEmail ?? string.Empty;
        dict[IntakeTokens.TicketSubject] = row.Value.TicketSubject ?? string.Empty;
        dict[IntakeTokens.TicketCategory] = row.Value.CategoryName ?? string.Empty;
        dict[IntakeTokens.TicketNumber] = row.Value.TicketNumber.ToString();
        dict[IntakeTokens.CompanyName] = row.Value.CompanyName ?? string.Empty;
        return dict;
    }

    private readonly record struct ContextRow(
        long TicketNumber,
        string? TicketSubject,
        string? CategoryName,
        string? RequesterEmail,
        string? RequesterName,
        string? CompanyName);
}
