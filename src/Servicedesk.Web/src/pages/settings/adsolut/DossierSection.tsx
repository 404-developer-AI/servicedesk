import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ApiError, adsolutApi, type AdsolutStatus } from "@/lib/api";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { ADSOLUT_STATUS_QUERY_KEY, ADSOLUT_ADMIN_QUERY_KEY, ADSOLUT_SYNC_QUERY_KEY } from "@/pages/settings/adsolut/shared";

export function DossierSection({ status: s }: { status: AdsolutStatus }) {
  const qc = useQueryClient();

  const administrations = useQuery({
    queryKey: ADSOLUT_ADMIN_QUERY_KEY,
    queryFn: () => adsolutApi.listAdministrations(),
  });

  const [dossierPick, setDossierPick] = useState<string>("");

  const selectAdministration = useMutation({
    mutationFn: (id: string) => adsolutApi.selectAdministration(id),
    onSuccess: () => {
      toast.success("Adsolut dossier activated");
      setDossierPick("");
      qc.invalidateQueries({ queryKey: ADSOLUT_STATUS_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_ADMIN_QUERY_KEY });
      qc.invalidateQueries({ queryKey: ADSOLUT_SYNC_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(
        err instanceof ApiError ? `Activate failed (${err.status})` : "Activate failed",
      ),
  });

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          Dossier
        </h2>
        <p className="text-xs text-muted-foreground">
          Pick which Adsolut administration this servicedesk install syncs against.
          Activating a dossier registers this integration with Wolters Kluwer; the
          disconnect flow deactivates it again so it does not keep generating billable
          activity at WK after the install stops using it.
        </p>
      </header>

      {administrations.isLoading ? (
        <Skeleton className="h-10 w-full" />
      ) : administrations.isError ? (
        <div className="rounded-md border border-rose-400/30 bg-rose-500/8 px-3 py-2 text-xs text-rose-200">
          Could not list dossiers — refresh-token may be expired. Try Test refresh first.
        </div>
      ) : (
        <div className="space-y-3">
          {s.administrationId ? (
            <div className="rounded-md border border-emerald-400/30 bg-emerald-500/6 px-3 py-2 text-xs text-emerald-200">
              Active dossier:{" "}
              <span className="font-mono">
                {administrations.data?.items.find((a) => a.id === s.administrationId)?.name ??
                  s.administrationId}
              </span>
              {(() => {
                const found = administrations.data?.items.find(
                  (a) => a.id === s.administrationId,
                );
                return found?.code ? (
                  <span className="ml-2 text-emerald-300/70">({found.code})</span>
                ) : null;
              })()}
            </div>
          ) : (
            <div className="rounded-md border border-amber-400/30 bg-amber-500/8 px-3 py-2 text-xs text-amber-200">
              No dossier picked yet — sync ticks are paused. Choose one below to start
              pulling Customers from Adsolut.
            </div>
          )}

          <div className="flex flex-wrap items-center gap-2">
            <Select
              value={dossierPick || s.administrationId || ""}
              onValueChange={(value) => setDossierPick(value)}
            >
              <SelectTrigger className="min-w-[16rem] flex-1">
                <SelectValue placeholder="Select a dossier…" />
              </SelectTrigger>
              <SelectContent>
                {(administrations.data?.items ?? []).map((a) => (
                  <SelectItem key={a.id} value={a.id}>
                    {a.name}
                    {a.code ? ` (${a.code})` : ""}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Button
              size="sm"
              onClick={() =>
                dossierPick && selectAdministration.mutate(dossierPick)
              }
              disabled={
                !dossierPick ||
                dossierPick === s.administrationId ||
                selectAdministration.isPending
              }
            >
              {selectAdministration.isPending ? "Activating…" : "Activate dossier"}
            </Button>
          </div>
        </div>
      )}
    </section>
  );
}
