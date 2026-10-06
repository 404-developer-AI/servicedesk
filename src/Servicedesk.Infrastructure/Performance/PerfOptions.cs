namespace Servicedesk.Infrastructure.Performance;

/// Performance monitoring level. <c>Diagnose</c> is never stored as the
/// base level: it is <c>Basic</c> plus a server-side expiry
/// (<c>Performance.DiagnoseUntilUtc</c>), so it always falls back on its own.
public enum PerfLevel
{
    Off = 0,
    Basic = 1,
    Diagnose = 2,
}

/// Individually switchable collectors (Settings → Performance → Settings).
[Flags]
public enum PerfCollector
{
    None = 0,
    Http = 1 << 0,
    Database = 1 << 1,
    Postgres = 1 << 2,
    Runtime = 1 << 3,
    Host = 1 << 4,
    Frontend = 1 << 5,
    SignalR = 1 << 6,
    Workers = 1 << 7,
    External = 1 << 8,
    All = Http | Database | Postgres | Runtime | Host | Frontend | SignalR | Workers | External,
}

/// Immutable snapshot of every performance setting the hot paths need. The
/// collectors read <see cref="IPerfSettings.Current"/> (one volatile field
/// read) — never the settings store — so a request never pays a lookup.
public sealed record PerfOptions(
    PerfLevel BaseLevel,
    DateTimeOffset? DiagnoseUntilUtc,
    PerfCollector Collectors,
    int RumSamplePercent,
    int SlowRequestThresholdMs,
    int SlowQueryThresholdMs,
    int NPlusOneThreshold,
    bool ServerTimingEnabled,
    int PgSnapshotIntervalMinutes,
    int PgSnapshotDiagnoseIntervalMinutes)
{
    public static readonly PerfOptions Disabled = new(
        PerfLevel.Off, null, PerfCollector.None, 0, 1000, 200, 5, false, 5, 1);

    /// Factory defaults — what a fresh install runs before the settings
    /// store has been read once.
    public static readonly PerfOptions Defaults = new(
        PerfLevel.Basic, null, PerfCollector.All, 10, 1000, 200, 5, true, 5, 1);

    /// The level in force at <paramref name="nowUtc"/>: Diagnose only while
    /// the expiry lies in the future and monitoring is not switched off.
    public PerfLevel EffectiveLevel(DateTimeOffset nowUtc)
    {
        if (BaseLevel == PerfLevel.Off) return PerfLevel.Off;
        return DiagnoseUntilUtc is { } until && until > nowUtc ? PerfLevel.Diagnose : PerfLevel.Basic;
    }
}
