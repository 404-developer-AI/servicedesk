import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Inbox, AlertTriangle, PauseCircle } from "lucide-react";
import { mailMailboxApi, type InboundMailbox } from "@/lib/ticket-api";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

const QUERY_KEY = ["admin", "mail", "mailboxes"] as const;

/// Per-source polling control. Lists every inbound-mailbox source (a queue can
/// have several) and lets an admin pause/resume Graph polling for each. Pausing
/// leaves the delta cursor untouched, so resuming continues from where it left
/// off.
export function InboundMailboxesSection() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({
    queryKey: QUERY_KEY,
    queryFn: () => mailMailboxApi.list(),
  });

  const toggle = useMutation({
    mutationFn: ({ sourceId, enabled }: { sourceId: string; enabled: boolean }) =>
      mailMailboxApi.setPolling(sourceId, enabled),
    // Optimistic flip so the switch feels instant; rolled back on error.
    onMutate: async ({ sourceId, enabled }) => {
      await qc.cancelQueries({ queryKey: QUERY_KEY });
      const prev = qc.getQueryData<InboundMailbox[]>(QUERY_KEY);
      qc.setQueryData<InboundMailbox[]>(QUERY_KEY, (old) =>
        old?.map((m) =>
          m.sourceId === sourceId ? { ...m, pollingEnabled: enabled } : m,
        ),
      );
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(QUERY_KEY, ctx.prev);
      toast.error("Could not change polling state");
    },
    onSuccess: (_r, { enabled }) =>
      toast.success(enabled ? "Polling resumed" : "Polling paused"),
    onSettled: () => qc.invalidateQueries({ queryKey: QUERY_KEY }),
  });

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-4 space-y-1">
        <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          Inbound mailboxes
        </h2>
        <p className="text-xs text-muted-foreground">
          Pause or resume Graph polling per mailbox. Pausing keeps the delta
          cursor, so resuming picks up where it left off instead of re-reading
          the whole inbox. A queue can have several inbound mailboxes.
        </p>
      </header>

      {isLoading ? (
        <div className="space-y-2">
          <Skeleton className="h-[60px] w-full rounded-lg" />
          <Skeleton className="h-[60px] w-full rounded-lg" />
        </div>
      ) : !data || data.length === 0 ? (
        <div className="rounded-lg border border-dashed border-glass-strong bg-glass px-6 py-6 text-center text-xs text-muted-foreground">
          No queues have an inbound mailbox configured yet. Set one under
          Settings → Tickets → Queues.
        </div>
      ) : (
        <div className="space-y-2">
          {data.map((m) => (
            <MailboxRow
              key={m.sourceId}
              mbx={m}
              busy={toggle.isPending}
              onToggle={(enabled) => toggle.mutate({ sourceId: m.sourceId, enabled })}
            />
          ))}
        </div>
      )}
    </section>
  );
}

function MailboxRow({
  mbx,
  busy,
  onToggle,
}: {
  mbx: InboundMailbox;
  busy: boolean;
  onToggle: (enabled: boolean) => void;
}) {
  const paused = !mbx.pollingEnabled;
  const hasError = !!mbx.lastError && mbx.consecutiveFailures > 0;

  return (
    <div
      className={cn(
        "flex items-center gap-3 rounded-lg border border-glass bg-glass-hover/40 px-3 py-3",
        paused && "opacity-70",
      )}
    >
      <div
        className={cn(
          "flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border",
          paused
            ? "border-glass bg-glass text-muted-foreground"
            : "border-sky-400/40 bg-sky-500/10 text-sky-500 dark:text-sky-300",
        )}
      >
        <Inbox className="h-4 w-4" />
      </div>

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="truncate text-sm font-medium text-foreground">{mbx.mailbox}</span>
          <span className="rounded-full border border-glass bg-glass px-1.5 py-0.5 text-[10px] text-muted-foreground">
            {mbx.queueName}
          </span>
          {paused && (
            <span className="inline-flex items-center gap-1 rounded-full border border-amber-400/40 bg-amber-500/10 px-1.5 py-0.5 text-[10px] font-medium text-amber-600 dark:text-amber-300">
              <PauseCircle className="h-3 w-3" /> Paused
            </span>
          )}
        </div>
        <div className="mt-0.5 flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
          <span>
            {mbx.folderConfigured
              ? `Folder: ${mbx.folderName || "selected"}`
              : "No inbound folder selected"}
          </span>
          {mbx.lastPolledUtc && (
            <span>· Last polled {new Date(mbx.lastPolledUtc).toLocaleString()}</span>
          )}
          {!mbx.isActive && <span>· Queue inactive</span>}
        </div>
        {hasError && (
          <div className="mt-1 inline-flex items-start gap-1 text-[11px] text-destructive">
            <AlertTriangle className="mt-px h-3 w-3 shrink-0" />
            <span className="line-clamp-2">
              {mbx.consecutiveFailures}× failed — {mbx.lastError}
            </span>
          </div>
        )}
      </div>

      <Switch
        checked={mbx.pollingEnabled}
        disabled={busy}
        onCheckedChange={(checked) => onToggle(checked)}
        aria-label={`Toggle polling for ${mbx.mailbox}`}
      />
    </div>
  );
}
