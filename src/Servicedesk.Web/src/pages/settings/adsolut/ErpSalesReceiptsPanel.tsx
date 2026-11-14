import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { RefreshCw } from "lucide-react";
import { ApiError, adsolutApi, type SettingEntry } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { SettingField } from "@/components/settings/SettingField";
import { findEntry, formatDate } from "@/pages/settings/adsolut/shared";

const ERP_RECEIPTS_QUERY_KEY = ["integrations", "adsolut", "erp-sales-receipts"] as const;

/// ERP SalesReceipts (verkoopbonnen) mirror controls. The enable toggle +
/// sync interval are plain settings (rendered via SettingField); the status
/// filter is a dynamic checkbox set discovered from the receipts seen during
/// sync, persisted through a dedicated endpoint. Surfaces the sync state +
/// a "Sync now" trigger.
export function ErpSalesReceiptsPanel({
  settingsEntries,
  settingsQueryKey,
}: {
  settingsEntries: SettingEntry[] | undefined;
  settingsQueryKey: readonly unknown[];
}) {
  const qc = useQueryClient();
  const state = useQuery({
    queryKey: ERP_RECEIPTS_QUERY_KEY,
    queryFn: () => adsolutApi.erpSalesReceiptsState(),
  });

  const enabledEntry = findEntry(settingsEntries, "Adsolut.Erp.SalesReceipts.Enabled");
  const intervalEntry = findEntry(settingsEntries, "Adsolut.Erp.SalesReceipts.SyncIntervalMinutes");

  const [selected, setSelected] = useState<string[]>([]);
  const [dirty, setDirty] = useState(false);
  const savedFilter = state.data?.statusFilter;
  // Re-sync the local checkbox state whenever the server's saved filter
  // changes (e.g. another admin saved, or the first sync surfaced statuses).
  useEffect(() => {
    setSelected(savedFilter ?? []);
    setDirty(false);
  }, [savedFilter?.join(",")]); // eslint-disable-line react-hooks/exhaustive-deps

  const saveFilter = useMutation({
    mutationFn: () => adsolutApi.setErpSalesReceiptsStatusFilter(selected),
    onSuccess: () => {
      toast.success("Status filter saved");
      setDirty(false);
      qc.invalidateQueries({ queryKey: ERP_RECEIPTS_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Save failed (${err.status})` : "Save failed"),
  });

  const triggerSync = useMutation({
    mutationFn: () => adsolutApi.triggerErpSalesReceiptsSync(),
    onSuccess: () => {
      toast.success("Sync queued — runs within a few seconds");
      // The tick writes its state shortly after; refetch once it likely landed.
      window.setTimeout(() => qc.invalidateQueries({ queryKey: ERP_RECEIPTS_QUERY_KEY }), 3500);
    },
    onError: (err) =>
      toast.error(
        err instanceof ApiError ? `Sync request failed (${err.status})` : "Sync request failed",
      ),
  });

  const toggleStatus = (code: string) => {
    setSelected((prev) =>
      prev.includes(code) ? prev.filter((c) => c !== code) : [...prev, code],
    );
    setDirty(true);
  };

  const d = state.data;
  const options = d?.statusOptions ?? [];

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Sales receipts (verkoopbonnen)
          </h2>
          <p className="text-xs text-muted-foreground">
            Mirror Adsolut ERP sales receipts into the Timesheet → Adsolut tab. Needs the{" "}
            <span className="font-mono">WK.BE.ERP.Read</span> scope (ticked above + reconnected).
            Each tick lists receipts, fetches each by-id for the full line detail, and stores
            header + product + performance lines. The total shown is excl. VAT.
          </p>
        </div>
        <Button
          size="sm"
          onClick={() => triggerSync.mutate()}
          disabled={triggerSync.isPending || enabledEntry?.value !== "true"}
        >
          <RefreshCw className="mr-1.5 h-3.5 w-3.5" />
          {triggerSync.isPending ? "Queueing…" : "Sync now"}
        </Button>
      </header>

      <div className="space-y-2">
        {enabledEntry && (
          <SettingField entry={enabledEntry} queryKey={settingsQueryKey} />
        )}
        {intervalEntry && (
          <SettingField entry={intervalEntry} queryKey={settingsQueryKey} />
        )}
      </div>

      {/* Status filter — dynamic checkboxes from the statuses seen in the
          mirror. Empty selection = keep all statuses. */}
      <div className="mt-4 rounded-md border border-glass-strong bg-glass p-4">
        <div className="mb-1 flex items-center justify-between gap-3">
          <h3 className="text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
            Status filter
          </h3>
          {dirty && <span className="text-[11px] text-amber-300/80">Unsaved changes</span>}
        </div>
        <p className="mb-3 text-xs text-muted-foreground/70">
          Tick the statuses to keep. None ticked = keep all. Deselected statuses are removed
          from the mirror on the next sync.
        </p>
        {options.length === 0 ? (
          <div className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
            No statuses discovered yet — enable the toggle and run a sync; the statuses found in
            your dossier will appear here.
          </div>
        ) : (
          <div className="space-y-1.5">
            {options.map((o) => (
              <label
                key={o.code}
                className="flex cursor-pointer items-center gap-3 rounded-md border border-glass bg-glass px-3 py-2 transition-colors hover:border-glass-strong"
              >
                <input
                  type="checkbox"
                  checked={selected.includes(o.code)}
                  onChange={() => toggleStatus(o.code)}
                  className="h-3.5 w-3.5 shrink-0 rounded border border-glass-strong bg-glass accent-purple-400"
                />
                <span className="flex-1 text-sm text-foreground">
                  {o.description ?? o.code}
                  <code className="ml-2 rounded bg-glass px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                    {o.code}
                  </code>
                </span>
                <span className="tabular-nums text-xs text-muted-foreground/70">{o.count}</span>
              </label>
            ))}
            <div className="flex justify-end pt-1">
              <Button
                size="sm"
                onClick={() => saveFilter.mutate()}
                disabled={!dirty || saveFilter.isPending}
              >
                {saveFilter.isPending ? "Saving…" : "Save status filter"}
              </Button>
            </div>
          </div>
        )}
      </div>

      {/* Sync state */}
      <dl className="mt-4 grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
        <dt className="text-muted-foreground/70">Receipts mirrored</dt>
        <dd className="text-foreground tabular-nums">{d?.totalMirrored ?? 0}</dd>
        <dt className="text-muted-foreground/70">Last sync</dt>
        <dd className="text-foreground">{formatDate(d?.lastDeltaSyncUtc)}</dd>
        {d?.nextSyncUtc && enabledEntry?.value === "true" ? (
          <>
            <dt className="text-muted-foreground/70">Next sync</dt>
            <dd className="text-foreground">
              {formatDate(d.nextSyncUtc)}
              <span className="ml-2 text-muted-foreground/60">(every {d.intervalMinutes} min)</span>
            </dd>
          </>
        ) : null}
        <dt className="text-muted-foreground/70">Seen / upserted (last tick)</dt>
        <dd className="text-foreground tabular-nums">
          {(d?.receiptsSeen ?? 0)} / {(d?.receiptsUpserted ?? 0)}
        </dd>
        {d?.lastError && (
          <>
            <dt className="text-muted-foreground/70">Last error</dt>
            <dd className="text-rose-300">
              {d.lastError}
              {d.lastErrorUtc && (
                <span className="ml-2 text-muted-foreground/60">({formatDate(d.lastErrorUtc)})</span>
              )}
            </dd>
          </>
        )}
      </dl>
    </section>
  );
}
