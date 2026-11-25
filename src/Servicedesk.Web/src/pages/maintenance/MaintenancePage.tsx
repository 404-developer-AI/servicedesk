import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useRouterState } from "@tanstack/react-router";
import { Pencil, Play, Plus, Search, Send, Wrench } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { formatDateMedium } from "@/lib/dateFormat";
import { errorMessage } from "@/lib/http";
import { cn } from "@/lib/utils";
import { MAINTENANCE_KEY, mailDateOf, maintenanceApi, maintenanceKeys, scheduleLabel, type MaintenancePlan } from "@/lib/maintenance-api";
import { minuteToTime } from "@/lib/timeOfDay";
import { MaintenancePlanDialog } from "./components/MaintenancePlanDialog";

/// v0.1.67 (Campaigns phase 3) — the Maintenance planner: recurring
/// interventions for several customers. On each plan's mail day every
/// customer gets a new ticket and the mail (automatically or as a draft
/// campaign); the answers per customer are on the round's campaign.
/// `?plan=<id>` opens a plan (global search). Same access as Campaigns.
export function MaintenancePage() {
  const [editing, setEditing] = React.useState<string | null | undefined>(undefined);
  const [search, setSearch] = React.useState("");
  const plans = useQuery({ queryKey: maintenanceKeys.plans(), queryFn: maintenanceApi.plans, refetchInterval: 60_000 });
  const searchStr = useRouterState({ select: (s) => s.location.searchStr });
  React.useEffect(() => {
    const id = new URLSearchParams(searchStr).get("plan");
    if (id) setEditing(id);
  }, [searchStr]);

  const q = search.trim().toLowerCase();
  const rows = (plans.data ?? []).filter((p) => !q || [p.name, p.notes].some((s) => s.toLowerCase().includes(q)));

  return (
    <div className="sd-maintenance flex min-h-0 w-full flex-1 flex-col gap-5">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">Maintenance</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Recurring interventions for several customers. Before each planned intervention every customer gets a new
            ticket and a mail asking whether the moment suits them; the answers are on the round's campaign.
          </p>
        </div>
        <Button onClick={() => setEditing(null)}><Plus className="mr-1.5 h-4 w-4" /> New plan</Button>
      </header>

      <div className="relative w-72">
        <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
        <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search plans…" className="pl-9" />
      </div>

      <div className="glass-panel min-h-64 overflow-auto">
        {plans.isLoading ? (
          <div className="space-y-2 p-4"><Skeleton className="h-10 w-full" /><Skeleton className="h-10 w-full" /></div>
        ) : plans.isError ? (
          <p className="p-6 text-sm text-destructive">Could not load the plans. Refresh to try again.</p>
        ) : rows.length === 0 ? (
          <div className="flex flex-col items-center gap-2 p-12 text-center">
            <Wrench className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">
              {(plans.data ?? []).length === 0 ? "No maintenance plans yet. Create one and add the customers it applies to." : "No plans match."}
            </p>
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead className="sticky top-0 z-10 bg-popover">
              <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                <th className="px-4 py-2.5 font-medium">Plan</th>
                <th className="px-3 py-2.5 font-medium">Next intervention</th>
                <th className="px-3 py-2.5 font-medium">Last round</th>
                <th className="px-4 py-2.5" />
              </tr>
            </thead>
            <tbody className="divide-y divide-glass">
              {rows.map((p) => <PlanRow key={p.id} plan={p} onEdit={() => setEditing(p.id)} />)}
            </tbody>
          </table>
        )}
      </div>

      <MaintenancePlanDialog open={editing !== undefined} onOpenChange={(o) => { if (!o) setEditing(undefined); }} planId={editing ?? null} />
    </div>
  );
}

function PlanRow({ plan: p, onEdit }: { plan: MaintenancePlan; onEdit: () => void }) {
  const qc = useQueryClient();
  const start = useMutation({
    mutationFn: () => maintenanceApi.startNow(p.id),
    onSuccess: () => {
      toast.success(p.autoSend ? "Round started — tickets created and mails queued" : "Round started — tickets created, the campaign waits for you");
      void qc.invalidateQueries({ queryKey: MAINTENANCE_KEY });
    },
    onError: (err) => toast.error(errorMessage(err, "Could not start the round")),
  });
  const r = p.lastRound;
  const open = r ? r.tickets - r.approved - r.otherTime - r.declined : 0;

  return (
    <tr className={cn("align-top hover:bg-glass/60", !p.isActive && "opacity-60")}>
      <td className="px-4 py-2.5">
        <div className="font-medium text-foreground">{p.name}{p.isActive ? "" : " (off)"}</div>
        <div className="text-[11px] text-muted-foreground">
          {p.memberCount} customer{p.memberCount === 1 ? "" : "s"} · {p.queueName ?? "—"} · {scheduleLabel(p)}
        </div>
      </td>
      <td className="px-3 py-2.5">
        {p.nextInterventionDate ? (
          <>
            <div>{formatDateMedium(p.nextInterventionDate)} · {minuteToTime(p.interventionMinute)}</div>
            <div className="text-[11px] text-muted-foreground">
              <span title="The planner checks every 15 minutes, so the round can start up to 15 minutes later.">
                mail on {formatDateMedium(mailDateOf(p.nextInterventionDate, p.mailDaysBefore))} · {minuteToTime(p.mailMinute)} (up to 15 min later)
              </span>
              {p.autoSend ? <span className="ml-1 inline-flex items-center gap-0.5 text-primary"><Send className="h-3 w-3" /> automatic</span> : " · draft"}
            </div>
          </>
        ) : <span className="text-muted-foreground">Not planned</span>}
      </td>
      <td className="px-3 py-2.5 text-xs">
        {r ? (
          <div className="space-y-0.5">
            <div>{formatDateMedium(r.interventionDate)}</div>
            {r.campaignId && (
              <Link to="/campaigns/$campaignId" params={{ campaignId: r.campaignId }} className="text-primary hover:underline">
                {r.campaignName}
              </Link>
            )}
            <div className="text-muted-foreground">
              {r.sent}/{r.tickets} mailed · {r.approved} approved · {r.otherTime} other moment · {r.declined} declined · {Math.max(0, open)} open{r.skipped > 0 ? ` · ${r.skipped} skipped` : ""}
            </div>
            {r.error && <div className="max-w-80 text-destructive" title={r.error}>{r.error}</div>}
            {r.reminderUtc ? (
              <div className="text-muted-foreground" title={r.reminderNote ?? undefined}>
                Reminder {formatDateMedium(r.reminderUtc)}: {r.reminderNote ?? "sending…"}
              </div>
            ) : p.reminderEnabled && p.autoSend && r.state === "Created" ? (
              <div className="text-muted-foreground">Reminder after {p.reminderDays} day{p.reminderDays === 1 ? "" : "s"} without an answer</div>
            ) : null}
          </div>
        ) : <span className="text-muted-foreground">Not run yet</span>}
      </td>
      <td className="px-4 py-2.5">
        <div className="flex justify-end gap-1">
          {p.isActive && p.nextInterventionDate && (
            <Button size="sm" variant="ghost" className="h-7 px-2 text-xs" disabled={start.isPending}
              title="Create the tickets for the next intervention now" onClick={() => start.mutate()}>
              <Play className="mr-1 h-3.5 w-3.5" /> Start now
            </Button>
          )}
          <Button size="sm" variant="ghost" className="h-7 px-2 text-xs" onClick={onEdit}>
            <Pencil className="mr-1 h-3.5 w-3.5" /> Edit
          </Button>
        </div>
      </td>
    </tr>
  );
}
