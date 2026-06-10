namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the Contracts worker — independent from
/// the other Adsolut sync signals so an admin can force a contracts pull without
/// disturbing the other cadences. Singleton (per-process state); concurrent
/// presses coalesce into one run.
public interface IAdsolutContractsSyncSignal
{
    void RequestImmediateRun();
    bool ConsumeRequest();
}

public sealed class AdsolutContractsSyncSignal : IAdsolutContractsSyncSignal
{
    private int _requested;

    public void RequestImmediateRun() => System.Threading.Interlocked.Exchange(ref _requested, 1);

    public bool ConsumeRequest() => System.Threading.Interlocked.Exchange(ref _requested, 0) == 1;
}
