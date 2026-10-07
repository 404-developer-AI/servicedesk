import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Clock, Flag, Globe2, Power, RefreshCw, Timer } from "lucide-react";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { settingsApi } from "@/lib/api";
import { authStore } from "@/auth/authStore";
import { Switch } from "@/components/ui/switch";
import { BusinessHoursTab } from "./sla/BusinessHoursTab";
import { HolidaysTab } from "./sla/HolidaysTab";
import { PoliciesTab } from "./sla/PoliciesTab";
import { FirstContactTab } from "./sla/FirstContactTab";
import { RecalcTab } from "./sla/RecalcTab";

type Tab = "hours" | "holidays" | "policies" | "first-contact" | "recalc";

const TABS: { id: Tab; label: string; icon: typeof Timer; description: string }[] = [
  { id: "hours", label: "Business hours", icon: Clock, description: "Weekly work schedule + timezone" },
  { id: "holidays", label: "Holidays", icon: Globe2, description: "Country-based auto-sync + overrides" },
  { id: "policies", label: "Policies", icon: Flag, description: "First-response + resolution targets per queue × priority" },
  { id: "first-contact", label: "First contact", icon: Timer, description: "Which events stop the first-response timer" },
  { id: "recalc", label: "Recalc worker", icon: RefreshCw, description: "Sweep cadence, batch size and policy cache" },
];

const SLA_LIST_KEY = ["settings", "list", "Sla"] as const;

/// v0.1.25 — the Sla.Enabled master switch. Off stops every SLA calculation
/// server-side and pauses SLA escalation triggers; nothing is deleted.
function SlaMasterSwitch() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: SLA_LIST_KEY, queryFn: () => settingsApi.list("Sla") });
  const enabled = q.data?.find((e) => e.key === "Sla.Enabled")?.value !== "false";
  const save = useMutation({
    mutationFn: (next: boolean) => settingsApi.update("Sla.Enabled", next ? "true" : "false"),
    onSuccess: (_d, next) => {
      toast.success(next ? "SLA switched on — open tickets are recalculated within a few minutes" : "SLA switched off");
      void qc.invalidateQueries({ queryKey: SLA_LIST_KEY });
      void qc.invalidateQueries({ queryKey: ["sla"] });
      // Nav entry + Due values follow the flag without a reload.
      const cur = authStore.get();
      if (cur.user) authStore.patch({ user: { ...cur.user, slaEnabled: next } });
    },
    onError: (e: Error) => toast.error(`Save failed: ${e.message}`),
  });

  return (
    <section
      className={cn(
        "flex flex-wrap items-start justify-between gap-4 rounded-lg border p-5",
        enabled ? "border-glass-strong bg-glass" : "border-amber-500/40 bg-amber-500/5",
      )}
    >
      <div className="flex min-w-0 max-w-2xl gap-3">
        <Power className={cn("mt-0.5 h-5 w-5 shrink-0", enabled ? "text-primary" : "text-amber-600 dark:text-amber-300")} />
        <div className="space-y-1">
          <h2 className="text-sm font-semibold text-foreground">
            SLA calculation {enabled ? "on" : "off"}
          </h2>
          <p className="text-xs text-muted-foreground">
            {enabled
              ? "Deadlines are calculated on every ticket change and refreshed by the background worker. Switch off to stop all SLA work: no calculation, no SLA escalation or warning triggers, and the SLA pill, Due values and SLA log are hidden."
              : "Nothing is calculated and SLA escalation / warning triggers do not fire. Existing SLA data is kept — switching back on recalculates the open tickets within a few minutes."}
          </p>
        </div>
      </div>
      <Switch
        checked={enabled}
        disabled={q.isLoading || save.isPending}
        onCheckedChange={(v) => save.mutate(v)}
        aria-label="SLA calculation"
      />
    </section>
  );
}

export function SlaSettingsPage() {
  const [tab, setTab] = useState<Tab>("hours");
  const active = TABS.find((t) => t.id === tab)!;

  return (
    <div className="flex flex-col gap-6">
      <header className="space-y-2">
        <div className="mb-2 text-primary">
          <Timer className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">SLA</h1>
        <p className="max-w-xl text-sm text-muted-foreground">
          Response and resolution targets, business hours, holidays and escalation policies. All
          timing is computed server-side against the selected business-hours schema.
        </p>
      </header>

      <SlaMasterSwitch />

      <nav className="flex flex-wrap gap-2">
        {TABS.map((t) => {
          const Icon = t.icon;
          const isActive = t.id === tab;
          return (
            <button
              key={t.id}
              type="button"
              onClick={() => setTab(t.id)}
              className={cn(
                "flex items-center gap-2 rounded-lg border px-3 py-2 text-sm transition",
                isActive
                  ? "border-primary/40 bg-primary/10 text-foreground"
                  : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
              )}
            >
              <Icon className="h-4 w-4" />
              {t.label}
            </button>
          );
        })}
      </nav>

      <section className="rounded-lg border border-glass-strong bg-glass p-5">
        <header className="mb-4 space-y-1">
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            {active.label}
          </h2>
          <p className="text-xs text-muted-foreground">{active.description}</p>
        </header>
        {tab === "hours" && <BusinessHoursTab />}
        {tab === "holidays" && <HolidaysTab />}
        {tab === "policies" && <PoliciesTab />}
        {tab === "first-contact" && <FirstContactTab />}
        {tab === "recalc" && <RecalcTab />}
      </section>
    </div>
  );
}
