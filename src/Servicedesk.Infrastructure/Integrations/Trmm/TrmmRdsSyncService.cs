using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Remote Desktop check sync (v0.1.10). Walks the mirrored server agents
/// (optionally workstations too), reads each agent's checks from TRMM,
/// picks the RDS script check by the configured name, parses its output
/// through <see cref="RdsCheckOutputParser"/> and upserts one
/// <c>trmm_agent_rds</c> row per agent.
///
/// Failure model: a per-agent HTTP/transport failure marks that one row
/// <c>error</c> and the cycle continues — a slow or restarting agent must
/// not hide the other 250 servers. Configuration-level failures (missing
/// key / base URL, 401) abort the cycle on the first agent, because
/// repeating them per server would be 257 identical audit rows.
public sealed class TrmmRdsSyncService : ITrmmRdsSyncService
{
    private const string FallbackOutputMarker = "Remote Desktop server:";

    private readonly ITrmmApiClient _api;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<TrmmRdsSyncService> _logger;

    public TrmmRdsSyncService(
        ITrmmApiClient api,
        NpgsqlDataSource dataSource,
        ISettingsService settings,
        IIntegrationAuditLogger audit,
        ILogger<TrmmRdsSyncService> logger)
    {
        _api = api;
        _dataSource = dataSource;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<TrmmRdsSyncOutcome> RunOnceAsync(string trigger, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await _audit.LogAsync(new IntegrationAuditEvent(
            Integration: TrmmEventTypes.Integration,
            EventType: TrmmEventTypes.RdsSyncStarted,
            Outcome: IntegrationAuditOutcome.Ok,
            Payload: new { trigger }), ct);

        try
        {
            var scriptName = (await _settings.GetAsync<string>(SettingKeys.Trmm.RdsCheckScriptName, ct) ?? string.Empty).Trim();
            var includeWorkstations = await _settings.GetAsync<bool>(SettingKeys.Trmm.RdsIncludeWorkstations, ct);

            await using var connection = await _dataSource.OpenConnectionAsync(ct);

            var agents = (await connection.QueryAsync<AgentRef>(new CommandDefinition(
                """
                SELECT trmm_agent_id AS TrmmAgentId, hostname AS Hostname
                  FROM trmm_agents
                 WHERE agent_type = 'server' OR @includeWorkstations
                 ORDER BY hostname
                """,
                new { includeWorkstations },
                cancellationToken: ct))).ToList();

            var counts = new Counts();
            foreach (var agent in agents)
            {
                ct.ThrowIfCancellationRequested();
                await SyncAgentAsync(connection, agent, scriptName, counts, ct);
            }

            stopwatch.Stop();

            // A cycle where every agent errored is a failed cycle: the
            // badge must go red rather than silently keep ageing.
            var allErrored = agents.Count > 0 && counts.Errors == agents.Count;
            var outcome = new TrmmRdsSyncOutcome(
                Success: !allErrored,
                Agents: agents.Count,
                Rds: counts.Rds,
                NotRds: counts.NotRds,
                Failed: counts.Failed,
                NoCheck: counts.NoCheck,
                Pending: counts.Pending,
                Errors: counts.Errors,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: allErrored ? "all_agents_errored" : null,
                ErrorMessage: allErrored ? "Every per-agent checks call failed — see the integration audit log." : null);

            await WriteSyncStateAsync(connection, outcome, ct);

            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: outcome.Success ? TrmmEventTypes.RdsSyncCompleted : TrmmEventTypes.RdsSyncFailed,
                Outcome: outcome.Success ? IntegrationAuditOutcome.Ok : IntegrationAuditOutcome.Error,
                LatencyMs: outcome.LatencyMs,
                ErrorCode: outcome.ErrorCode,
                Payload: new
                {
                    trigger,
                    agents = outcome.Agents,
                    rds = outcome.Rds,
                    notRds = outcome.NotRds,
                    failed = outcome.Failed,
                    noCheck = outcome.NoCheck,
                    pending = outcome.Pending,
                    errors = outcome.Errors,
                }), ct);

            return outcome;
        }
        catch (TrmmApiException ex)
        {
            stopwatch.Stop();
            var outcome = FailedOutcome(ex.UpstreamErrorCode ?? "transport_error", ex.Message, stopwatch);
            await TryPersistFailureStateAsync(outcome, ct);
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: TrmmEventTypes.RdsSyncFailed,
                Outcome: IntegrationAuditOutcome.Error,
                LatencyMs: outcome.LatencyMs,
                ErrorCode: outcome.ErrorCode,
                Payload: new { trigger, message = outcome.ErrorMessage }), ct);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "TRMM RDS sync threw an unexpected exception.");
            var outcome = FailedOutcome("internal_error", ex.Message, stopwatch);
            await TryPersistFailureStateAsync(outcome, ct);
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: TrmmEventTypes.RdsSyncFailed,
                Outcome: IntegrationAuditOutcome.Error,
                LatencyMs: outcome.LatencyMs,
                ErrorCode: "internal_error",
                Payload: new { trigger, message = ex.Message }), ct);
            return outcome;
        }
    }

    private async Task SyncAgentAsync(
        NpgsqlConnection connection,
        AgentRef agent,
        string scriptName,
        Counts counts,
        CancellationToken ct)
    {
        IReadOnlyList<TrmmAgentCheck> checks;
        try
        {
            checks = await _api.ListAgentChecksAsync(agent.TrmmAgentId, ct);
        }
        catch (TrmmApiException ex) when (!IsConfigurationFailure(ex))
        {
            counts.Errors++;
            await UpsertAsync(connection, new RdsRow(
                agent.TrmmAgentId, RdsStatus.Error, null, null, null, null, null,
                null, null, null, null, Truncate(ex.Message, 500)), ct);
            return;
        }

        var check = SelectRdsCheck(checks, scriptName);
        if (check is null)
        {
            counts.NoCheck++;
            await UpsertAsync(connection, new RdsRow(
                agent.TrmmAgentId, RdsStatus.NoCheck, null, null, null, null, null,
                null, null, null, null, null), ct);
            return;
        }

        var parsed = RdsCheckOutputParser.Parse(check.Stdout, check.Retcode, check.ResultStatus);
        switch (parsed.Status)
        {
            case RdsStatus.Rds: counts.Rds++; break;
            case RdsStatus.NotRds: counts.NotRds++; break;
            case RdsStatus.Pending: counts.Pending++; break;
            default: counts.Failed++; break;
        }

        await UpsertAsync(connection, new RdsRow(
            agent.TrmmAgentId,
            parsed.Status,
            check.Id,
            check.ResultStatus,
            check.Retcode,
            Truncate(check.Stdout, 8000),
            Truncate(check.Stderr, 8000),
            parsed.LastLoginLocal,
            parsed.LastLoginKind,
            parsed.LastLoginUser,
            check.LastRunUtc,
            null), ct);
    }

    /// Picks the RDS check among an agent's checks. Name match first
    /// (substring, case-insensitive, against the check name and TRMM's
    /// readable description); when nothing matches by name — or no name
    /// is configured — any script check whose output carries the
    /// "Remote Desktop server:" line is accepted. Several candidates
    /// collapse to the most recently run one.
    internal static TrmmAgentCheck? SelectRdsCheck(IReadOnlyList<TrmmAgentCheck> checks, string scriptName)
    {
        var scripts = checks
            .Where(c => c.CheckType is null || c.CheckType.Equals("script", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (scripts.Count == 0) return null;

        IEnumerable<TrmmAgentCheck> candidates = Array.Empty<TrmmAgentCheck>();
        if (scriptName.Length > 0)
        {
            candidates = scripts.Where(c =>
                (c.Name?.Contains(scriptName, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.ReadableDesc?.Contains(scriptName, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var byName = candidates.ToList();
        if (byName.Count == 0)
        {
            byName = scripts
                .Where(c => c.Stdout?.Contains(FallbackOutputMarker, StringComparison.OrdinalIgnoreCase) ?? false)
                .ToList();
        }
        if (byName.Count == 0) return null;

        return byName
            .OrderByDescending(c => c.LastRunUtc ?? DateTime.MinValue)
            .ThenBy(c => c.Id)
            .First();
    }

    private static bool IsConfigurationFailure(TrmmApiException ex) =>
        ex.UpstreamErrorCode is "api_key_missing" or "base_url_missing" or "invalid_credentials";

    private static async Task UpsertAsync(NpgsqlConnection connection, RdsRow row, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO trmm_agent_rds
                (trmm_agent_id, status, check_id, check_status, retcode, stdout, stderr,
                 last_login_local, last_login_kind, last_login_user, last_run_utc,
                 fetched_utc, fetch_error)
            VALUES
                (@trmmAgentId, @status, @checkId, @checkStatus, @retcode, @stdout, @stderr,
                 @lastLoginLocal, @lastLoginKind, @lastLoginUser, @lastRunUtc,
                 now(), @fetchError)
            ON CONFLICT (trmm_agent_id) DO UPDATE SET
                status           = EXCLUDED.status,
                check_id         = EXCLUDED.check_id,
                check_status     = EXCLUDED.check_status,
                retcode          = EXCLUDED.retcode,
                stdout           = EXCLUDED.stdout,
                stderr           = EXCLUDED.stderr,
                last_login_local = EXCLUDED.last_login_local,
                last_login_kind  = EXCLUDED.last_login_kind,
                last_login_user  = EXCLUDED.last_login_user,
                last_run_utc     = EXCLUDED.last_run_utc,
                fetched_utc      = now(),
                fetch_error      = EXCLUDED.fetch_error
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            trmmAgentId = row.TrmmAgentId,
            status = row.Status,
            checkId = row.CheckId,
            checkStatus = row.CheckStatus,
            retcode = row.Retcode,
            stdout = row.Stdout,
            stderr = row.Stderr,
            lastLoginLocal = row.LastLoginLocal,
            lastLoginKind = row.LastLoginKind,
            lastLoginUser = row.LastLoginUser,
            lastRunUtc = row.LastRunUtc,
            fetchError = row.FetchError,
        }, cancellationToken: ct));
    }

    private static async Task WriteSyncStateAsync(
        NpgsqlConnection connection, TrmmRdsSyncOutcome outcome, CancellationToken ct)
    {
        var countsJson = JsonSerializer.Serialize(new
        {
            outcome.Agents,
            outcome.Rds,
            outcome.NotRds,
            outcome.Failed,
            outcome.NoCheck,
            outcome.Pending,
            outcome.Errors,
            outcome.LatencyMs,
        });
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE trmm_sync_state SET
                last_rds_sync_utc = now(),
                last_rds_status   = @status,
                last_rds_error    = @error,
                last_rds_counts   = @counts::jsonb
            WHERE id = 'singleton'
            """,
            new
            {
                status = outcome.Success ? "ok" : "failed",
                error = outcome.Success ? null : outcome.ErrorMessage,
                counts = countsJson,
            },
            cancellationToken: ct));
    }

    private async Task TryPersistFailureStateAsync(TrmmRdsSyncOutcome outcome, CancellationToken ct)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await WriteSyncStateAsync(connection, outcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist TRMM RDS sync failure state.");
        }
    }

    private static TrmmRdsSyncOutcome FailedOutcome(string code, string message, Stopwatch stopwatch) =>
        new(
            Success: false,
            Agents: 0, Rds: 0, NotRds: 0, Failed: 0, NoCheck: 0, Pending: 0, Errors: 0,
            LatencyMs: (int)stopwatch.ElapsedMilliseconds,
            ErrorCode: code,
            ErrorMessage: message);

    private static string? Truncate(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max]);

    private sealed class AgentRef
    {
        public string TrmmAgentId { get; set; } = "";
        public string Hostname { get; set; } = "";
    }

    private sealed class Counts
    {
        public int Rds, NotRds, Failed, NoCheck, Pending, Errors;
    }

    private sealed record RdsRow(
        string TrmmAgentId,
        string Status,
        long? CheckId,
        string? CheckStatus,
        long? Retcode,
        string? Stdout,
        string? Stderr,
        DateTime? LastLoginLocal,
        string? LastLoginKind,
        string? LastLoginUser,
        DateTime? LastRunUtc,
        string? FetchError);
}
