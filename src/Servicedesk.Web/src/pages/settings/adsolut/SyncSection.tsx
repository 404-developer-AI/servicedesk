import { useMutation, useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { RefreshCw } from "lucide-react";
import { adsolutApi, type AdsolutSyncState } from "@/lib/adsolut-api";
import type { SettingEntry } from "@/lib/settings-api";
import { Button } from "@/components/ui/button";
import { SettingField } from "@/components/settings/SettingField";
import { ADSOLUT_SETTINGS_QUERY_KEY, ADSOLUT_COVERAGE_QUERY_KEY } from "@/pages/settings/adsolut/shared";
import { CoverageTile } from "@/pages/settings/adsolut/CoverageTile";
import { formatDateTimeMedium } from "@/lib/dateFormat";
import { findEntry } from "@/components/settings/settingEntries";
import { errorMessage } from "@/lib/http";

export function SyncSection({
  syncState,
  settingsEntries,
}: {
  syncState: AdsolutSyncState | undefined;
  settingsEntries: SettingEntry[] | undefined;
}) {
  // v0.0.30 — coverage tile counts. Refreshes via SignalR
  // (IntegrationSyncCompleted invalidates the coverage queryKey) so an
  // admin sees gap counts move after each sync tick without a navigation.
  const coverage = useQuery({
    queryKey: ADSOLUT_COVERAGE_QUERY_KEY,
    queryFn: () => adsolutApi.coverageCounts(),
  });

  const triggerSync = useMutation({
    mutationFn: () => adsolutApi.triggerSync(),
    onSuccess: () => {
      toast.success("Sync queued — will run within 2 seconds");
      // Audit log + sync-state will refresh on the SignalR push when the
      // tick lands; nothing to invalidate here yet.
    },
    onError: (err) =>
      toast.error(
        errorMessage(err, "Sync request failed"),
      ),
  });

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div className="space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Sync
          </h2>
          <p className="text-xs text-muted-foreground">
            Bidirectional Adsolut ↔ servicedesk Companies sync. Pull and Push toggles are
            independent — an admin can keep updates flowing in both directions while never
            creating new rows on either side. All gating-toggles default OFF and are reset
            to OFF on every (re)connect; opt in once you've verified the dossier mapping.
          </p>
        </div>
        <Button
          size="sm"
          onClick={() => triggerSync.mutate()}
          disabled={triggerSync.isPending}
        >
          <RefreshCw className="mr-1.5 h-3.5 w-3.5" />
          {triggerSync.isPending ? "Queueing…" : "Sync now"}
        </Button>
      </header>

      <dl className="mb-4 grid grid-cols-1 gap-y-2 text-xs sm:grid-cols-2">
        <dt className="text-muted-foreground/70">Last delta sync</dt>
        <dd className="text-foreground">{formatDateTimeMedium(syncState?.lastDeltaSyncUtc)}</dd>
        <dt className="text-muted-foreground/70">Last full sync</dt>
        <dd className="text-foreground">{formatDateTimeMedium(syncState?.lastFullSyncUtc)}</dd>
        <dt className="text-muted-foreground/70">Companies seen (last tick)</dt>
        <dd className="text-foreground tabular-nums">
          {syncState?.companiesSeen ?? 0}
        </dd>
        <dt className="text-muted-foreground/70">Companies upserted (last tick)</dt>
        <dd className="text-foreground tabular-nums">
          {syncState?.companiesUpserted ?? 0}
        </dd>
        <dt className="text-muted-foreground/70">Skipped — local edit newer</dt>
        <dd className="text-foreground tabular-nums">
          {syncState?.companiesSkippedLoserInConflict ?? 0}
        </dd>
        {syncState?.lastError && (
          <>
            <dt className="text-muted-foreground/70">Last error</dt>
            <dd className="text-rose-300">
              {syncState.lastError}
              {syncState.lastErrorUtc && (
                <span className="ml-2 text-muted-foreground/60">
                  ({formatDateTimeMedium(syncState.lastErrorUtc)})
                </span>
              )}
            </dd>
          </>
        )}
      </dl>

      {/* v0.0.30 — Sync coverage tile. Five raw bucket counts; the two
          drift counts are conditionally suppressed when the relevant
          push-toggle is ON, because in that case the push-tak resolves
          the gap within one tick and the row would just bounce. */}
      <CoverageTile
        counts={coverage.data}
        isLoading={coverage.isLoading}
        pushUpdateOn={
          findEntry(settingsEntries, "Adsolut.Push.UpdateExistingCustomers")?.value ===
          "true"
        }
        pushContactsUpdateOn={
          findEntry(settingsEntries, "Adsolut.Sync.Push.Contacts.Update")?.value ===
          "true"
        }
      />

      <div className="space-y-5">
        <div className="space-y-2">
          {(
            ["Adsolut.Sync.IntervalMinutes"] as const
          ).map((key) => {
            const entry = findEntry(settingsEntries, key);
            if (!entry) return null;
            return (
              <SettingField
                key={key}
                entry={entry}
                queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
              />
            );
          })}
        </div>

        <div>
          <h3 className="mb-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
            Pull · Adsolut → servicedesk
          </h3>
          <div className="space-y-2">
            {(
              [
                "Adsolut.Sync.Pull.Companies.Update",
                "Adsolut.Sync.Pull.Companies.Create",
                "Adsolut.Sync.Pull.Contacts.Update",
                "Adsolut.Sync.Pull.Contacts.Create",
                "Adsolut.Sync.Contacts.ReconcileIntervalHours",
              ] as const
            ).map((key) => {
              const entry = findEntry(settingsEntries, key);
              if (!entry) return null;
              return (
                <SettingField
                  key={key}
                  entry={entry}
                  queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                />
              );
            })}
          </div>
        </div>

        <div>
          <h3 className="mb-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
            Push · Servicedesk → Adsolut
          </h3>
          <div className="space-y-2">
            {(
              [
                "Adsolut.Push.UpdateExistingCustomers",
                "Adsolut.Push.CreateNewCustomers",
                "Adsolut.Sync.Push.Contacts.Update",
                "Adsolut.Sync.Push.Contacts.Create",
              ] as const
            ).map((key) => {
              const entry = findEntry(settingsEntries, key);
              if (!entry) return null;
              return (
                <SettingField
                  key={key}
                  entry={entry}
                  queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                />
              );
            })}
          </div>
        </div>

        {/* Suppliers — placeholder for v0.0.28. The IncludeSuppliers toggle and
            the two push-toggles are seeded so the v0.0.28 unlock is a code flip,
            not a schema migration. The backend force-ignores them in v0.0.27. */}
        <div>
          <h3 className="mb-2 flex items-center gap-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
            Suppliers
            <span className="rounded-full border border-amber-400/30 bg-amber-500/8 px-2 py-0.5 text-[9px] uppercase tracking-wider text-amber-200">
              In development
            </span>
          </h3>
          <p className="mb-2 text-xs text-muted-foreground/70">
            Bidirectional Suppliers sync is targeted for the next release. These toggles
            are visible but the worker ignores them until v0.0.28 lands.
          </p>
          <div className="space-y-2 opacity-60">
            {(
              [
                "Adsolut.Sync.IncludeSuppliers",
                "Adsolut.Push.UpdateExistingSuppliers",
                "Adsolut.Push.CreateNewSuppliers",
              ] as const
            ).map((key) => {
              const entry = findEntry(settingsEntries, key);
              if (!entry) return null;
              return (
                <SettingField
                  key={key}
                  entry={entry}
                  queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                  readOnly
                />
              );
            })}
          </div>
        </div>

        <div>
          <h3 className="mb-2 text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
            Behavior
          </h3>
          <div className="space-y-2">
            {(
              ["Adsolut.Sync.LinkCompanyDomainsFromEmail"] as const
            ).map((key) => {
              const entry = findEntry(settingsEntries, key);
              if (!entry) return null;
              return (
                <SettingField
                  key={key}
                  entry={entry}
                  queryKey={ADSOLUT_SETTINGS_QUERY_KEY}
                />
              );
            })}
          </div>
        </div>
      </div>
    </section>
  );
}
