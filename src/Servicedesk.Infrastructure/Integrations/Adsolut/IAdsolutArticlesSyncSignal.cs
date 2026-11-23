using Servicedesk.Infrastructure.Hosting;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the Articles worker — independent from
/// the Companies + SalesReceipts + Orders sync signals so an admin can force an
/// articles pull without disturbing the other cadences. Singleton (per-process
/// state); concurrent presses coalesce into one run.
public interface IAdsolutArticlesSyncSignal : IWorkerWakeSignal;

public sealed class AdsolutArticlesSyncSignal : WorkerWakeSignal, IAdsolutArticlesSyncSignal;
