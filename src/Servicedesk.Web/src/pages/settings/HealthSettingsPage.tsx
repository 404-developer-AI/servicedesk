import * as React from "react";
import { Activity, AlertTriangle, CheckCircle2, ChevronDown, ChevronRight } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ApiError,
  healthApi,
  type HealthStatus,
  type IncidentRow,
  type SubsystemHealth,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";

const HEALTH_QUERY_KEY = ["admin", "health"] as const;
const INCIDENTS_QUERY_KEY = ["admin", "health", "incidents"] as const;
// Re-using the dashboard pill's key so resetting refreshes both views.
const PILL_QUERY_KEY = ["system", "health"] as const;

const STATUS_BADGE: Record<HealthStatus, { label: string; className: string; icon: React.ReactNode }> = {
  Ok: {
    label: "OK",
    className: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    icon: <CheckCircle2 className="h-3.5 w-3.5" />,
  },
  Warning: {
    label: "Warning",
    className: "border-amber-400/30 bg-amber-500/10 text-amber-300",
    icon: <AlertTriangle className="h-3.5 w-3.5" />,
  },
  Critical: {
    label: "Critical",
    className: "border-rose-400/40 bg-rose-500/10 text-rose-300",
    icon: <AlertTriangle className="h-3.5 w-3.5" />,
  },
};

const SEVERITY_BADGE: Record<IncidentRow["severity"], string> = {
  Warning: "border-amber-400/30 bg-amber-500/10 text-amber-300",
  Critical: "border-rose-400/40 bg-rose-500/10 text-rose-300",
};

export function HealthSettingsPage() {
  const qc = useQueryClient();
  const query = useQuery({
    queryKey: HEALTH_QUERY_KEY,
    queryFn: () => healthApi.get(),
    refetchInterval: 30_000,
  });
  const incidentsQuery = useQuery({
    queryKey: INCIDENTS_QUERY_KEY,
    queryFn: () => healthApi.listIncidents(200),
    refetchInterval: 30_000,
  });

  const invalidateAll = () => {
    qc.invalidateQueries({ queryKey: HEALTH_QUERY_KEY });
    qc.invalidateQueries({ queryKey: INCIDENTS_QUERY_KEY });
    qc.invalidateQueries({ queryKey: PILL_QUERY_KEY });
  };

  const runAction = useMutation({
    mutationFn: async (endpoint: string) => healthApi.runAction(endpoint),
    onSuccess: () => {
      toast.success("Action applied");
      invalidateAll();
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Failed (${err.status})` : "Action failed"),
  });

  const ackOne = useMutation({
    mutationFn: async (id: number) => healthApi.acknowledge(id),
    onSuccess: () => {
      toast.success("Incident acknowledged");
      invalidateAll();
    },
    onError: () => toast.error("Failed to acknowledge"),
  });

  const ackSubsystem = useMutation({
    mutationFn: async (subsystem: string) => healthApi.acknowledgeSubsystem(subsystem),
    onSuccess: (data) => {
      toast.success(`Acknowledged ${data.acknowledged} incident(s)`);
      invalidateAll();
    },
    onError: () => toast.error("Failed to acknowledge"),
  });

  const incidents = incidentsQuery.data?.items ?? [];
  const bySubsystem = React.useMemo(() => {
    const m = new Map<string, IncidentRow[]>();
    for (const inc of incidents) {
      const list = m.get(inc.subsystem) ?? [];
      list.push(inc);
      m.set(inc.subsystem, list);
    }
    return m;
  }, [incidents]);

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Activity className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Health</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Live status of background subsystems. Warnings and errors from
            captured log sources accumulate as incidents until acknowledged.
          </p>
        </div>
        {query.data ? (
          <Badge className={`border text-xs font-normal ${STATUS_BADGE[query.data.status].className}`}>
            <span className="mr-1">{STATUS_BADGE[query.data.status].icon}</span>
            {STATUS_BADGE[query.data.status].label}
          </Badge>
        ) : null}
      </header>

      {query.isLoading ? (
        <div className="space-y-3">
          <Skeleton className="h-28 w-full" />
          <Skeleton className="h-28 w-full" />
        </div>
      ) : query.data ? (
        <div className="flex flex-col gap-3">
          {query.data.subsystems.map((s) => (
            <SubsystemCard
              key={s.key}
              subsystem={s}
              incidents={bySubsystem.get(s.key) ?? []}
              onAction={(endpoint, confirmMessage) => {
                if (confirmMessage && !window.confirm(confirmMessage)) return;
                runAction.mutate(endpoint);
              }}
              onAckOne={(id) => ackOne.mutate(id)}
              onAckAll={(key) => {
                if (!window.confirm(`Acknowledge all open incidents for ${key}?`)) return;
                ackSubsystem.mutate(key);
              }}
              pending={runAction.isPending || ackOne.isPending || ackSubsystem.isPending}
            />
          ))}
        </div>
      ) : null}
    </div>
  );
}

function SubsystemCard({
  subsystem,
  incidents,
  onAction,
  onAckOne,
  onAckAll,
  pending,
}: {
  subsystem: SubsystemHealth;
  incidents: IncidentRow[];
  onAction: (endpoint: string, confirmMessage: string | null) => void;
  onAckOne: (id: number) => void;
  onAckAll: (subsystem: string) => void;
  pending: boolean;
}) {
  const badge = STATUS_BADGE[subsystem.status];
  const openIncidents = incidents.filter((i) => !i.acknowledgedUtc);
  const [expanded, setExpanded] = React.useState(openIncidents.length > 0);

  return (
    <section className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 space-y-1">
          <div className="flex items-center gap-2">
            <h2 className="text-sm font-semibold text-foreground">{subsystem.label}</h2>
            <Badge className={`border text-[11px] font-normal ${badge.className}`}>
              {badge.label}
            </Badge>
          </div>
          <p className="text-sm text-muted-foreground">{subsystem.summary}</p>
        </div>
      </div>

      {subsystem.details.length > 0 ? (
        <dl className="mt-4 grid gap-2 text-sm sm:grid-cols-[220px_1fr]">
          {subsystem.details.map((d, i) => (
            <div key={i} className="contents">
              <dt className="text-muted-foreground">{d.label}</dt>
              <dd className="text-foreground break-words">{d.value ?? "—"}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      {subsystem.actions.length > 0 ? (
        <div className="mt-4 flex flex-wrap gap-2">
          {subsystem.actions.map((a) => (
            <Button
              key={a.key}
              size="sm"
              variant="outline"
              disabled={pending}
              onClick={() => onAction(a.endpoint, a.confirmMessage)}
            >
              {a.label}
            </Button>
          ))}
        </div>
      ) : null}

      {incidents.length > 0 ? (
        <div className="mt-4 rounded-md border border-white/[0.06] bg-black/20">
          <button
            type="button"
            onClick={() => setExpanded((v) => !v)}
            className="flex w-full items-center justify-between gap-2 px-3 py-2 text-left text-xs text-muted-foreground hover:text-foreground"
          >
            <span className="flex items-center gap-2">
              {expanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
              Incidents ({openIncidents.length} open · {incidents.length - openIncidents.length} acknowledged)
            </span>
            {openIncidents.length > 0 ? (
              <Button
                size="sm"
                variant="ghost"
                disabled={pending}
                onClick={(e) => {
                  e.stopPropagation();
                  onAckAll(subsystem.key);
                }}
              >
                Acknowledge all
              </Button>
            ) : null}
          </button>
          {expanded ? (
            <ul className="divide-y divide-white/[0.04] border-t border-white/[0.04]">
              {incidents.map((inc) => (
                <IncidentEntry
                  key={inc.id}
                  incident={inc}
                  onAck={() => onAckOne(inc.id)}
                  pending={pending}
                />
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}

function IncidentEntry({
  incident,
  onAck,
  pending,
}: {
  incident: IncidentRow;
  onAck: () => void;
  pending: boolean;
}) {
  const [showDetails, setShowDetails] = React.useState(false);
  const acked = !!incident.acknowledgedUtc;

  return (
    <li className="px-3 py-2 text-xs">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0 flex-1 space-y-1">
          <div className="flex items-center gap-2">
            <Badge className={`border text-[10px] font-normal ${SEVERITY_BADGE[incident.severity]}`}>
              {incident.severity}
            </Badge>
            {acked ? (
              <Badge className="border border-white/10 bg-white/[0.02] text-[10px] font-normal text-muted-foreground">
                Acknowledged
              </Badge>
            ) : null}
            {incident.occurrenceCount > 1 ? (
              <span className="text-muted-foreground">×{incident.occurrenceCount}</span>
            ) : null}
            <span className="text-muted-foreground">
              {new Date(incident.lastOccurredUtc).toLocaleString()}
            </span>
          </div>
          <p className="whitespace-pre-wrap break-words text-foreground/90">{incident.message}</p>
          {incident.details ? (
            <button
              type="button"
              className="text-[11px] text-primary hover:underline"
              onClick={() => setShowDetails((v) => !v)}
            >
              {showDetails ? "Hide details" : "Show details"}
            </button>
          ) : null}
          {showDetails && incident.details ? (
            <pre className="mt-1 max-h-64 overflow-auto rounded border border-white/[0.06] bg-black/40 p-2 text-[11px] text-foreground/80">
              {incident.details}
            </pre>
          ) : null}
        </div>
        {!acked ? (
          <Button size="sm" variant="outline" disabled={pending} onClick={onAck}>
            Acknowledge
          </Button>
        ) : null}
      </div>
    </li>
  );
}
