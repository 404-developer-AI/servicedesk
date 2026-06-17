using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// <inheritdoc cref="IClaudeUsageStore"/>
public sealed class ClaudeUsageStore : IClaudeUsageStore
{
    private readonly NpgsqlDataSource _dataSource;

    public ClaudeUsageStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task LogAsync(ClaudeUsageEntry entry, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO claude_usage_log
                (user_id, ticket_id, model, input_tokens, output_tokens,
                 cost_micro_eur, image_count, outcome, error_code, request_id)
            VALUES
                (@UserId, @TicketId, @Model, @InputTokens, @OutputTokens,
                 @CostMicroEur, @ImageCount, @Outcome, @ErrorCode, @RequestId)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            entry.UserId,
            entry.TicketId,
            entry.Model,
            entry.InputTokens,
            entry.OutputTokens,
            entry.CostMicroEur,
            entry.ImageCount,
            entry.Outcome,
            entry.ErrorCode,
            entry.RequestId,
        }, cancellationToken: ct));
    }

    public async Task<long> GetMonthSpendMicroEurAsync(Guid userId, DateTime monthStartUtc, CancellationToken ct)
    {
        const string sql = """
            SELECT COALESCE(SUM(cost_micro_eur), 0)::bigint
              FROM claude_usage_log
             WHERE user_id = @userId AND utc >= @monthStart
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, new { userId, monthStart = monthStartUtc }, cancellationToken: ct));
    }

    public async Task<int?> GetUserBudgetOverrideCentsAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT claude_monthly_budget_eur_cents
              FROM users WHERE id = @userId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            sql, new { userId }, cancellationToken: ct));
    }

    public async Task SetUserBudgetOverrideCentsAsync(Guid userId, int? cents, CancellationToken ct)
    {
        const string sql = """
            UPDATE users SET claude_monthly_budget_eur_cents = @cents WHERE id = @userId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { userId, cents }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ClaudeAgentUsage>> GetAgentMonthlyUsageAsync(DateTime monthStartUtc, CancellationToken ct)
    {
        const string sql = """
            SELECT  u.id                              AS UserId,
                    u.email                           AS Email,
                    u.role_name                       AS RoleName,
                    u.claude_monthly_budget_eur_cents AS BudgetOverrideCents,
                    COALESCE(SUM(l.cost_micro_eur), 0)::bigint AS MonthSpendMicroEur,
                    COALESCE(SUM(CASE WHEN l.outcome <> 'blocked' THEN 1 ELSE 0 END), 0)::int AS CallCount
              FROM users u
              LEFT JOIN claude_usage_log l
                     ON l.user_id = u.id AND l.utc >= @monthStart
             WHERE u.role_name IN ('Admin', 'Agent')
             GROUP BY u.id, u.email, u.role_name, u.claude_monthly_budget_eur_cents
             ORDER BY MonthSpendMicroEur DESC, u.email ASC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClaudeAgentUsage>(new CommandDefinition(
            sql, new { monthStart = monthStartUtc }, cancellationToken: ct));
        return rows.ToList();
    }
}
