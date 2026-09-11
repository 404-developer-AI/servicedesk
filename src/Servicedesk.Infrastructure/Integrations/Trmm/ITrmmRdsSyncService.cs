namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// One Remote Desktop (RDS) check sync cycle (v0.1.10): for every
/// mirrored server agent, read its checks from TRMM, pick the RDS script
/// check by name, parse the output and upsert <c>trmm_agent_rds</c>.
/// Runs on its own cadence (<c>Trmm.RdsSyncIntervalMinutes</c>) because it
/// costs one TRMM call per server, unlike the three-call agent mirror.
public interface ITrmmRdsSyncService
{
    Task<TrmmRdsSyncOutcome> RunOnceAsync(string trigger, CancellationToken ct);
}

/// Per-status counts of one RDS sync cycle. <see cref="Errors"/> counts
/// agents whose checks call failed (the row is kept with status
/// <c>error</c>); a cycle where <i>every</i> agent errored is reported as
/// a failed cycle so the badge turns red instead of silently ageing.
public sealed record TrmmRdsSyncOutcome(
    bool Success,
    int Agents,
    int Rds,
    int NotRds,
    int Failed,
    int NoCheck,
    int Pending,
    int Errors,
    int LatencyMs,
    string? ErrorCode,
    string? ErrorMessage);
