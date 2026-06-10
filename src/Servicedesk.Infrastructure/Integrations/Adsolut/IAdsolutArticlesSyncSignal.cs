namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the Articles worker — independent from
/// the Companies + SalesReceipts + Orders sync signals so an admin can force an
/// articles pull without disturbing the other cadences. Singleton (per-process
/// state); concurrent presses coalesce into one run.
public interface IAdsolutArticlesSyncSignal
{
    void RequestImmediateRun();
    bool ConsumeRequest();
}

public sealed class AdsolutArticlesSyncSignal : IAdsolutArticlesSyncSignal
{
    private int _requested;

    public void RequestImmediateRun() => System.Threading.Interlocked.Exchange(ref _requested, 1);

    public bool ConsumeRequest() => System.Threading.Interlocked.Exchange(ref _requested, 0) == 1;
}
