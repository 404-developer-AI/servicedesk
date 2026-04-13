import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Shield } from "lucide-react";
import {
  queueAccessApi,
  taxonomyApi,
  type Queue,
} from "@/lib/api";
import { userApi, type AgentUser } from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

// ---- Agent queue row ----

function AgentQueueRow({
  agent,
  queues,
}: {
  agent: AgentUser;
  queues: Queue[];
}) {
  const qc = useQueryClient();

  const { data: access, isLoading } = useQuery({
    queryKey: ["queue-access", agent.id],
    queryFn: () => queueAccessApi.getForUser(agent.id),
  });

  const grantedIds = access?.queueIds ?? [];

  const save = useMutation({
    mutationFn: (ids: string[]) => queueAccessApi.setForUser(agent.id, ids),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["queue-access", agent.id] });
      toast.success(`Queue access updated for ${agent.email}`);
    },
    onError: () => {
      toast.error("Failed to update queue access");
    },
  });

  function toggleQueue(queueId: string) {
    const next = grantedIds.includes(queueId)
      ? grantedIds.filter((id) => id !== queueId)
      : [...grantedIds, queueId];
    save.mutate(next);
  }

  return (
    <div className="flex flex-col gap-2 rounded-lg border border-white/[0.06] bg-white/[0.02] px-4 py-3">
      <div className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <p className="truncate text-sm font-medium text-foreground">{agent.email}</p>
          <p className="text-xs text-muted-foreground">{agent.roleName}</p>
        </div>
        {isLoading && <Skeleton className="h-4 w-16 rounded" />}
        {!isLoading && (
          <span className="shrink-0 text-xs text-muted-foreground">
            {grantedIds.length} / {queues.length} queue{queues.length !== 1 ? "s" : ""}
          </span>
        )}
      </div>

      {!isLoading && queues.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {queues.map((queue) => {
            const granted = grantedIds.includes(queue.id);
            return (
              <button
                key={queue.id}
                type="button"
                disabled={save.isPending}
                onClick={() => toggleQueue(queue.id)}
                className={cn(
                  "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium transition-colors",
                  "disabled:opacity-50 disabled:cursor-not-allowed",
                  granted
                    ? "border-emerald-500/40 bg-emerald-500/15 text-emerald-400"
                    : "border-white/[0.08] bg-white/[0.02] text-muted-foreground hover:border-white/20 hover:text-foreground",
                )}
              >
                <span
                  className="h-1.5 w-1.5 rounded-full shrink-0"
                  style={{ backgroundColor: queue.color ?? "#6366f1" }}
                />
                {queue.name}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ---- Loading skeletons ----

function AgentRowSkeleton() {
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-white/[0.06] bg-white/[0.02] px-4 py-3">
      <div className="flex items-center justify-between">
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-4 w-16" />
      </div>
      <div className="flex gap-1.5">
        {Array.from({ length: 3 }).map((_, i) => (
          <Skeleton key={i} className="h-5 w-20 rounded-full" />
        ))}
      </div>
    </div>
  );
}

// ---- Page ----

export function QueueAccessSettingsPage() {
  const { data: agents, isLoading: agentsLoading } = useQuery({
    queryKey: ["agents"],
    queryFn: () => userApi.listAgents(),
  });

  const { data: queues = [], isLoading: queuesLoading } = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });

  const loading = agentsLoading || queuesLoading;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <Shield className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground leading-tight">
              Queue Access
            </h1>
            {!loading && (
              <p className="text-xs text-muted-foreground">
                {agents?.length ?? 0} agent{agents?.length !== 1 ? "s" : ""},{" "}
                {queues.length} queue{queues.length !== 1 ? "s" : ""}
              </p>
            )}
          </div>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <div className="space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60 px-1 mb-3">
          Agents
        </h2>

        {loading ? (
          <div className="space-y-2">
            {Array.from({ length: 4 }).map((_, i) => (
              <AgentRowSkeleton key={i} />
            ))}
          </div>
        ) : !agents || agents.length === 0 ? (
          <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-6 py-10 flex flex-col items-center justify-center gap-3 text-center">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10 border border-primary/20">
              <Shield className="h-5 w-5 text-primary/60" />
            </div>
            <div>
              <p className="text-sm font-medium text-foreground">No agents yet</p>
              <p className="text-xs text-muted-foreground mt-1">
                Agents will appear here once they have been created.
              </p>
            </div>
          </div>
        ) : (
          <div className="space-y-2">
            {agents.map((agent) => (
              <AgentQueueRow key={agent.id} agent={agent} queues={queues} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
