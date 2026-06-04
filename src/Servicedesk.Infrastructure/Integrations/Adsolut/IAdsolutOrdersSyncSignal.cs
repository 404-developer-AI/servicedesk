namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the Orders worker — independent from
/// the Companies + SalesReceipts sync signals so an admin (or the ticket
/// "Sync orders" button) can force an orders pull without disturbing the other
/// cadences. Singleton (per-process state); concurrent presses coalesce into
/// one run.
public interface IAdsolutOrdersSyncSignal
{
    void RequestImmediateRun();
    bool ConsumeRequest();
}

public sealed class AdsolutOrdersSyncSignal : IAdsolutOrdersSyncSignal
{
    private int _requested;

    public void RequestImmediateRun() => System.Threading.Interlocked.Exchange(ref _requested, 1);

    public bool ConsumeRequest() => System.Threading.Interlocked.Exchange(ref _requested, 0) == 1;
}
