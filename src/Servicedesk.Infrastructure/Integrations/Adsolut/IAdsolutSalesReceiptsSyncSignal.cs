using Servicedesk.Infrastructure.Hosting;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Cross-process "Sync now" signal for the SalesReceipts worker — independent
/// from the Companies sync signal so an admin can force a receipts pull
/// without disturbing the companies cadence. Singleton (per-process state);
/// concurrent presses coalesce into one run.
public interface IAdsolutSalesReceiptsSyncSignal : IWorkerWakeSignal;

public sealed class AdsolutSalesReceiptsSyncSignal : WorkerWakeSignal, IAdsolutSalesReceiptsSyncSignal;
