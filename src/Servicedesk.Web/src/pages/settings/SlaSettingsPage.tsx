import { useState } from "react";
import { Clock, Flag, Globe2, RefreshCw, Timer } from "lucide-react";
import { cn } from "@/lib/utils";
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
