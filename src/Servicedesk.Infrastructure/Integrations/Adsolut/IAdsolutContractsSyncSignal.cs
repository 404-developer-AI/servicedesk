using Servicedesk.Infrastructure.Hosting;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the Contracts worker — independent from
/// the other Adsolut sync signals so an admin can force a contracts pull without
/// disturbing the other cadences. Singleton (per-process state); concurrent
/// presses coalesce into one run.
public interface IAdsolutContractsSyncSignal : IWorkerWakeSignal;

public sealed class AdsolutContractsSyncSignal : WorkerWakeSignal, IAdsolutContractsSyncSignal;
