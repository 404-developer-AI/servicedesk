import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Save } from "lucide-react";
import { toast } from "sonner";
import { settingsApi } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";

const LIST_KEY = ["settings", "list", "Sla"] as const;

const FIELDS = [
  {
    key: "Sla.RecalcIntervalSeconds",
    label: "Sweep interval (seconds)",
    description: "How often the background worker runs one sweep cycle over open tickets. Minimum 15.",
    min: 15,
    max: 86400,
  },
  {
    key: "Sla.RecalcBatchSize",
    label: "Tickets per cycle",
    description:
      "Open tickets recalculated per cycle. The worker walks the whole open set across cycles, so a full pass takes ⌈open tickets ÷ batch⌉ cycles.",
    min: 10,
    max: 5000,
  },
  {
    key: "Sla.ConfigCacheSeconds",
    label: "Policy cache (seconds)",
    description:
      "How long policies, business hours and holidays stay in memory before being re-read. Edits on these tabs apply immediately on this instance; this only bounds staleness for a second app instance during an update. 0 = re-read every time.",
    min: 0,
    max: 3600,
  },
] as const;

type Key = (typeof FIELDS)[number]["key"];

export function RecalcTab() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: LIST_KEY, queryFn: () => settingsApi.list("Sla") });
  const [values, setValues] = useState<Record<Key, string>>({
    "Sla.RecalcIntervalSeconds": "60",
    "Sla.RecalcBatchSize": "500",
    "Sla.ConfigCacheSeconds": "300",
  });

  useEffect(() => {
    if (!q.data) return;
    setValues((prev) => {
      const next = { ...prev };
      for (const f of FIELDS) {
        const entry = q.data.find((e) => e.key === f.key);
        if (entry) next[f.key] = entry.value;
      }
      return next;
    });
  }, [q.data]);

  const invalid = FIELDS.filter((f) => {
    const n = Number(values[f.key]);
    return !Number.isInteger(n) || n < f.min || n > f.max;
  });

  const save = useMutation({
    mutationFn: async () => {
      for (const f of FIELDS) {
        await settingsApi.update(f.key, String(Number(values[f.key])));
      }
    },
    onSuccess: () => {
      toast.success("Recalc settings saved — the worker picks them up on its next cycle");
      qc.invalidateQueries({ queryKey: LIST_KEY });
    },
    onError: (e: Error) => toast.error(`Save failed: ${e.message}`),
  });

  if (q.isLoading) return <Skeleton className="h-48 w-full" />;

  const interval = Number(values["Sla.RecalcIntervalSeconds"]);
  const batch = Number(values["Sla.RecalcBatchSize"]);
  const passExample =
    Number.isFinite(interval) && Number.isFinite(batch) && batch > 0
      ? `${Math.ceil(2000 / batch)} cycles ≈ ${Math.round((Math.ceil(2000 / batch) * interval) / 60)} min`
      : "—";

  return (
    <div className="flex flex-col gap-4">
      <p className="text-xs text-muted-foreground">
        The recalc worker keeps deadlines, paused time and breach flags fresh for open tickets even
        when nothing happens on them. Every ticket mutation still recalculates that ticket
        immediately; the sweep only covers time passing. Resolved and closed tickets are not swept —
        their SLA numbers are frozen until they are reopened.
      </p>

      <div className="grid gap-3 md:grid-cols-3">
        {FIELDS.map((f) => {
          const n = Number(values[f.key]);
          const bad = !Number.isInteger(n) || n < f.min || n > f.max;
          return (
            <label
              key={f.key}
              className="flex flex-col gap-2 rounded-md border border-glass-strong bg-glass p-3 hover:bg-glass-hover"
            >
              <span className="text-sm font-medium text-foreground">{f.label}</span>
              <input
                type="number"
                min={f.min}
                max={f.max}
                value={values[f.key]}
                onChange={(e) => setValues((prev) => ({ ...prev, [f.key]: e.target.value }))}
                className={
                  "rounded-md border bg-background/60 px-2 py-1 text-sm text-foreground outline-none focus:ring-2 focus:ring-primary/40 " +
                  (bad ? "border-destructive/60" : "border-glass-strong")
                }
              />
              <span className="text-xs text-muted-foreground">{f.description}</span>
              <span className="text-[11px] text-muted-foreground/70">
                Allowed: {f.min}–{f.max}
              </span>
            </label>
          );
        })}
      </div>

      <p className="text-xs text-muted-foreground">
        With these values, 2 000 open tickets take a full pass of <span className="font-medium">{passExample}</span>.
      </p>

      <div>
        <Button onClick={() => save.mutate()} disabled={save.isPending || invalid.length > 0}>
          {save.isPending ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Save className="mr-2 h-4 w-4" />}
          Save
        </Button>
      </div>
    </div>
  );
}
