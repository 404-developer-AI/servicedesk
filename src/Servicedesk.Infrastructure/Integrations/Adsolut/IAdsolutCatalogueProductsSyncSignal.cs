using Servicedesk.Infrastructure.Hosting;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the CatalogueProducts worker —
/// independent from the other Adsolut sync signals so an admin can force a
/// catalogue pull (from the Timesheet → work-hours article manager) without
/// disturbing the other cadences. Singleton (per-process state); concurrent
/// presses coalesce into one run.
public interface IAdsolutCatalogueProductsSyncSignal : IWorkerWakeSignal;

public sealed class AdsolutCatalogueProductsSyncSignal : WorkerWakeSignal, IAdsolutCatalogueProductsSyncSignal;
