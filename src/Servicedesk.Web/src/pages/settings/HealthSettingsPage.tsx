import { Activity, AlertTriangle, CheckCircle2 } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ApiError, healthApi, type HealthStatus, type SubsystemHealth } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";

const HEALTH_QUERY_KEY = ["admin", "health"] as const;
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

export function HealthSettingsPage() {
  const qc = useQueryClient();
  const query = useQuery({
    queryKey: HEALTH_QUERY_KEY,
    queryFn: () => healthApi.get(),
    refetchInterval: 30_000,
  });

  const runAction = useMutation({
    mutationFn: async (endpoint: string) => healthApi.runAction(endpoint),
    onSuccess: () => {
      toast.success("Action applied");
      qc.invalidateQueries({ queryKey: HEALTH_QUERY_KEY });
      qc.invalidateQueries({ queryKey: PILL_QUERY_KEY });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Failed (${err.status})` : "Action failed"),
  });

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Activity className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Health</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Live status of background subsystems. Issues here affect mail intake,
            automation, and storage — follow the action button on a card to retry or
            troubleshoot.
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
              onAction={(endpoint, confirmMessage) => {
                if (confirmMessage && !window.confirm(confirmMessage)) return;
                runAction.mutate(endpoint);
              }}
              pending={runAction.isPending}
            />
          ))}
        </div>
      ) : null}
    </div>
  );
}

function SubsystemCard({
  subsystem,
  onAction,
  pending,
}: {
  subsystem: SubsystemHealth;
  onAction: (endpoint: string, confirmMessage: string | null) => void;
  pending: boolean;
}) {
  const badge = STATUS_BADGE[subsystem.status];
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
    </section>
  );
}
