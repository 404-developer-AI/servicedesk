namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the CatalogueProducts worker —
/// independent from the other Adsolut sync signals so an admin can force a
/// catalogue pull (from the Timesheet → work-hours article manager) without
/// disturbing the other cadences. Singleton (per-process state); concurrent
/// presses coalesce into one run.
public interface IAdsolutCatalogueProductsSyncSignal
{
    void RequestImmediateRun();
    bool ConsumeRequest();
}

public sealed class AdsolutCatalogueProductsSyncSignal : IAdsolutCatalogueProductsSyncSignal
{
    private int _requested;

    public void RequestImmediateRun() => System.Threading.Interlocked.Exchange(ref _requested, 1);

    public bool ConsumeRequest() => System.Threading.Interlocked.Exchange(ref _requested, 0) == 1;
}
