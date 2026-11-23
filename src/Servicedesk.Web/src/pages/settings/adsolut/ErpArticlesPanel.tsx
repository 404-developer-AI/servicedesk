import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { RefreshCw } from "lucide-react";
import { ApiError, adsolutApi, type SettingEntry } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { SettingField } from "@/components/settings/SettingField";
import { findEntry } from "@/pages/settings/adsolut/shared";
import { formatDateTimeMedium } from "@/lib/dateFormat";

const ERP_ARTICLES_QUERY_KEY = ["integrations", "adsolut", "erp-articles"] as const;

/// v0.0.76 — ERP Articles (artikels) mirror panel. Simple reference-list slice:
/// an enable toggle + interval + sync state + "Sync now". No status filter (an
/// article has no status, only an active flag). Feeds the Contract Articles
/// list (Contracts → Contract Articles); per-user access is the Contracts flag.
export function ErpArticlesPanel({
  settingsEntries,
  settingsQueryKey,
}: {
  settingsEntries: SettingEntry[] | undefined;
  settingsQueryKey: readonly unknown[];
}) {
  const qc = useQueryClient();
  const state = useQuery({
    queryKey: ERP_ARTICLES_QUERY_KEY,
    queryFn: () => adsolutApi.erpArticlesState(),
  });

  const enabledEntry = findEntry(settingsEntries, "Adsolut.Erp.Articles.Enabled");
  const intervalEntry = findEntry(settingsEntries, "Adsolut.Erp.Articles.SyncIntervalMinutes");

  const triggerSync = useMutation({
    mutationFn: () => adsolutApi.triggerErpArticlesSync(),
    onSuccess: () => {
      toast.success("Sync queued — runs within a few seconds");
      window.setTimeout(() => qc.invalidateQueries({ queryKey: ERP_ARTICLES_QUERY_KEY }), 3500);
    },
    onError: (err) =>
      toast.error(
        err instanceof ApiError ? `Sync request failed (${err.status})` : "Sync request failed",
      ),
  });

  const d = state.data;

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Articles (artikels)
          </h2>
          <p className="text-xs text-muted-foreground">
            Mirror the Adsolut ERP article catalogue into the Contract Articles list (Contracts →
            Contract Articles). Needs the <span className="font-mono">WK.BE.ERP.Read</span> scope
            (ticked above + reconnected). Per-user access is the{" "}
            <span className="font-mono">Contracts</span> feature flag.
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

      {/* Sync state */}
      <dl className="mt-4 grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
        <dt className="text-muted-foreground/70">Articles mirrored</dt>
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
          {(d?.articlesSeen ?? 0)} / {(d?.articlesUpserted ?? 0)}
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
