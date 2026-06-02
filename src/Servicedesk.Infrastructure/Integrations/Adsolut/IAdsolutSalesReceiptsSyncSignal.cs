namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the SalesReceipts worker — independent
/// from the Companies sync signal so an admin can force a receipts pull
/// without disturbing the companies cadence. Singleton (per-process state);
/// concurrent presses coalesce into one run.
public interface IAdsolutSalesReceiptsSyncSignal
{
    void RequestImmediateRun();
    bool ConsumeRequest();
}

public sealed class AdsolutSalesReceiptsSyncSignal : IAdsolutSalesReceiptsSyncSignal
{
    private int _requested;

    public void RequestImmediateRun() => System.Threading.Interlocked.Exchange(ref _requested, 1);

    public bool ConsumeRequest() => System.Threading.Interlocked.Exchange(ref _requested, 0) == 1;
}
