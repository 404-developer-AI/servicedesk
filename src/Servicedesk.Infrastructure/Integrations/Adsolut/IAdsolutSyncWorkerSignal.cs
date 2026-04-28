namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process signal so the admin UI can ask the sync worker to run a
/// tick on demand without waiting for the next interval. The worker resets
/// the flag when it picks it up; concurrent presses coalesce into one
/// run. Singleton lifetime (per-process state).
public interface IAdsolutSyncWorkerSignal
{
    void RequestImmediateRun();
    bool ConsumeRequest();
}

public sealed class AdsolutSyncWorkerSignal : IAdsolutSyncWorkerSignal
{
    private int _requested;

    public void RequestImmediateRun()
    {
        System.Threading.Interlocked.Exchange(ref _requested, 1);
    }

    public bool ConsumeRequest()
    {
        return System.Threading.Interlocked.Exchange(ref _requested, 0) == 1;
    }
}
