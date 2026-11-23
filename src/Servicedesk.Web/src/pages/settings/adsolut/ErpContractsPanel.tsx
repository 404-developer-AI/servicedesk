import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { RefreshCw } from "lucide-react";
import { adsolutApi } from "@/lib/adsolut-api";
import type { AdsolutErpContractsState } from "@/lib/adsolut-erp-api";
import type { SettingEntry } from "@/lib/settings-api";
import { Button } from "@/components/ui/button";
import { SettingField } from "@/components/settings/SettingField";
import { findEntry } from "@/components/settings/settingEntries";
import { formatDateTimeMedium } from "@/lib/dateFormat";
import { errorMessage } from "@/lib/http";

const ERP_CONTRACTS_QUERY_KEY = ["integrations", "adsolut", "erp-contracts"] as const;

/// ERP Contracts (contracten) mirror panel. Mirrors the orders panel pattern:
/// an enable toggle + interval + display-only status filter (the mirror always
/// holds every status; the filter only narrows what the overview surfaces) +
/// sync state + "Sync now".
export function ErpContractsPanel({
  settingsEntries,
  settingsQueryKey,
}: {
  settingsEntries: SettingEntry[] | undefined;
  settingsQueryKey: readonly unknown[];
}) {
  const qc = useQueryClient();
  const state = useQuery({
    queryKey: ERP_CONTRACTS_QUERY_KEY,
    queryFn: () => adsolutApi.erpContractsState(),
  });

  const enabledEntry = findEntry(settingsEntries, "Adsolut.Erp.Contracts.Enabled");
  const intervalEntry = findEntry(settingsEntries, "Adsolut.Erp.Contracts.SyncIntervalMinutes");

  const [selected, setSelected] = useState<string[]>([]);
  const [dirty, setDirty] = useState(false);
  const savedFilter = (state.data as AdsolutErpContractsState | undefined)?.statusFilter;
  useEffect(() => {
    setSelected(savedFilter ?? []);
    setDirty(false);
  }, [savedFilter?.join(",")]); // eslint-disable-line react-hooks/exhaustive-deps

  const saveFilter = useMutation({
    mutationFn: () => adsolutApi.setErpContractsStatusFilter(selected),
    onSuccess: () => {
      toast.success("Status filter saved");
      setDirty(false);
      qc.invalidateQueries({ queryKey: ERP_CONTRACTS_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(errorMessage(err, "Save failed")),
  });

  const triggerSync = useMutation({
    mutationFn: () => adsolutApi.triggerErpContractsSync(),
    onSuccess: () => {
      toast.success("Sync queued — runs within a few seconds");
      window.setTimeout(() => qc.invalidateQueries({ queryKey: ERP_CONTRACTS_QUERY_KEY }), 3500);
    },
    onError: (err) =>
      toast.error(
        errorMessage(err, "Sync request failed"),
      ),
  });

  const toggleStatus = (code: string) => {
    setSelected((prev) =>
      prev.includes(code) ? prev.filter((c) => c !== code) : [...prev, code],
    );
    setDirty(true);
  };

  // Contract-status colours. Local map owned after the first load; persisted
  // on Save. Drives the coloured status pill in the Contracts overview.
  const [colors, setColors] = useState<Record<string, string>>({});
  const [colorsDirty, setColorsDirty] = useState(false);
  const savedColors = (state.data as AdsolutErpContractsState | undefined)?.statusColors;
  useEffect(() => {
    setColors(savedColors ?? {});
    setColorsDirty(false);
  }, [JSON.stringify(savedColors ?? {})]); // eslint-disable-line react-hooks/exhaustive-deps

  const saveColors = useMutation({
    mutationFn: () => adsolutApi.setErpContractsStatusColors(colors),
    onSuccess: () => {
      toast.success("Status colours saved");
      setColorsDirty(false);
      qc.invalidateQueries({ queryKey: ERP_CONTRACTS_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(errorMessage(err, "Save failed")),
  });

  const setColor = (code: string, hex: string) => {
    setColors((prev) => ({ ...prev, [code]: hex }));
    setColorsDirty(true);
  };

  const d = state.data as AdsolutErpContractsState | undefined;
  const options = d?.statusOptions ?? [];

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Contracts (contracten)
          </h2>
          <p className="text-xs text-muted-foreground">
            Mirror Adsolut ERP contracts into the Contracts overview (Contracts → Contracts overview).
            Needs the <span className="font-mono">WK.BE.ERP.Read</span> scope (ticked above + reconnected).
            Per-user access is the <span className="font-mono">Contracts</span> feature flag.
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
        {enabledEntry && <SettingField entry={enabledEntry} queryKey={settingsQueryKey} />}
        {intervalEntry && <SettingField entry={intervalEntry} queryKey={settingsQueryKey} />}
      </div>

      {/* Status filter — DISPLAY-ONLY. Dynamic checkboxes from the statuses
          seen in the mirror. Empty selection = show all. */}
      <div className="mt-4 rounded-md border border-glass-strong bg-glass p-4">
        <div className="mb-1 flex items-center justify-between gap-3">
          <h3 className="text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
            Status filter
          </h3>
          {dirty && <span className="text-[11px] text-amber-300/80">Unsaved changes</span>}
        </div>
        <p className="mb-3 text-xs text-muted-foreground/70">
          Tick the statuses to show in the Contracts overview + global search. None ticked = show all.
          This is display-only — every status stays mirrored; deselecting only hides, and re-ticking
          shows again with no re-sync.
        </p>
        {options.length === 0 ? (
          <div className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
            No statuses discovered yet — enable the toggle and run a sync; the contract statuses found in
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

      {/* Contract-status colours — a colour per discovered contract status.
          Drives the coloured status pill in the Contracts overview. */}
      <div className="mt-4 rounded-md border border-glass-strong bg-glass p-4">
        <div className="mb-1 flex items-center justify-between gap-3">
          <h3 className="text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
            Status colours
          </h3>
          {colorsDirty && <span className="text-[11px] text-amber-300/80">Unsaved changes</span>}
        </div>
        <p className="mb-3 text-xs text-muted-foreground/70">
          Give each contract status a colour — the status pill in the Contracts overview takes it.
          A status without a colour shows as a neutral pill.
        </p>
        {options.length === 0 ? (
          <div className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground">
            No statuses discovered yet — enable the toggle and run a sync; the contract statuses found
            in your dossier will appear here.
          </div>
        ) : (
          <div className="space-y-1.5">
            {options.map((o) => (
              <label
                key={o.code}
                className="flex items-center gap-3 rounded-md border border-glass bg-glass px-3 py-2"
              >
                <input
                  type="color"
                  value={colors[o.code] ?? "#9ca3af"}
                  onChange={(e) => setColor(o.code, e.target.value)}
                  className="h-6 w-9 shrink-0 cursor-pointer rounded border border-glass-strong bg-transparent"
                  aria-label={`Colour for ${o.code}`}
                />
                <span className="flex-1 text-sm text-foreground">
                  {o.description ?? o.code}
                  <code className="ml-2 rounded bg-glass px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                    {o.code}
                  </code>
                </span>
              </label>
            ))}
            <div className="flex justify-end pt-1">
              <Button
                size="sm"
                onClick={() => saveColors.mutate()}
                disabled={!colorsDirty || saveColors.isPending}
              >
                {saveColors.isPending ? "Saving…" : "Save colours"}
              </Button>
            </div>
          </div>
        )}
      </div>

      {/* Sync state */}
      <dl className="mt-4 grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
        <dt className="text-muted-foreground/70">Contracts mirrored</dt>
        <dd className="text-foreground tabular-nums">{d?.totalMirrored ?? 0}</dd>
        <dt className="text-muted-foreground/70">Last sync</dt>
        <dd className="text-foreground">{formatDateTimeMedium(d?.lastDeltaSyncUtc)}</dd>
        {d?.nextSyncUtc && enabledEntry?.value === "true" ? (
          <>
            <dt className="text-muted-foreground/70">Next sync</dt>
            <dd className="text-foreground">
              {formatDateTimeMedium(d.nextSyncUtc)}
              <span className="ml-2 text-muted-foreground/60">(every {d.intervalMinutes} min)</span>
            </dd>
          </>
        ) : null}
        <dt className="text-muted-foreground/70">Seen / upserted (last tick)</dt>
        <dd className="text-foreground tabular-nums">
          {(d?.contractsSeen ?? 0)} / {(d?.contractsUpserted ?? 0)}
        </dd>
        {d?.lastError && (
          <>
            <dt className="text-muted-foreground/70">Last error</dt>
            <dd className="text-rose-300">
              {d.lastError}
              {d.lastErrorUtc && (
                <span className="ml-2 text-muted-foreground/60">({formatDateTimeMedium(d.lastErrorUtc)})</span>
              )}
            </dd>
          </>
        )}
      </dl>
    </section>
  );
}
