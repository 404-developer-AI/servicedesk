using Servicedesk.Infrastructure.Hosting;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process signal so the admin UI can ask the sync worker to run a
/// tick on demand without waiting for the next interval. The worker resets
/// the flag when it picks it up; concurrent presses coalesce into one
/// run. Singleton lifetime (per-process state).
public interface IAdsolutSyncWorkerSignal : IWorkerWakeSignal;

public sealed class AdsolutSyncWorkerSignal : WorkerWakeSignal, IAdsolutSyncWorkerSignal;
