import * as React from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Beaker, CheckCircle2, AlertCircle, MinusCircle, XCircle, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { triggerApi, type TriggerDryRunAction, type TriggerDryRunResult } from "@/lib/api";
import { ticketApi, type TicketPickerItem } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

type Props = {
  triggerId: string;
  /// True when the form has unsaved edits — dry-run still runs against
  /// the saved version of the trigger, so we surface a clear hint.
  dirty: boolean;
};

/// Blok 7 — admin test-runner. Picks a real ticket, runs the trigger
/// against it through the read-only previewer pipeline, and renders the
/// per-action diff. Always reads the SAVED trigger row server-side, so
/// unsaved edits in the editor are not reflected — that's intentional:
/// admins should be able to validate "what is in production" against a
/// ticket without saving experimental changes first.
export function TestRunner({ triggerId, dirty }: Props) {
  const [query, setQuery] = React.useState("");
  const [picked, setPicked] = React.useState<TicketPickerItem | null>(null);
  const [result, setResult] = React.useState<TriggerDryRunResult | null>(null);

  const debouncedQuery = useDebounced(query, 200);

  const pickerQ = useQuery({
    queryKey: ["tickets", "picker", debouncedQuery],
    queryFn: () => ticketApi.picker(debouncedQuery, undefined, 10),
    enabled: debouncedQuery.trim().length > 0 && !picked,
    staleTime: 30_000,
  });

  const runM = useMutation({
    mutationFn: () => {
      if (!picked) throw new Error("Pick a ticket first.");
      return triggerApi.dryRun(triggerId, picked.id);
    },
    onSuccess: (r) => setResult(r),
    onError: () => setResult(null),
  });

  function reset() {
    setPicked(null);
    setQuery("");
    setResult(null);
  }

  return (
    <div className="space-y-4">
      {dirty && (
        <div className="rounded-md border border-amber-500/30 bg-amber-500/[0.08] px-3 py-2 text-xs text-amber-200/90">
          Unsaved edits — dry-run uses the saved trigger row. Save first to test changes.
        </div>
      )}

      {/* Ticket picker */}
      {!picked ? (
        <div className="space-y-2">
          <label className="text-xs font-medium text-muted-foreground">Pick a ticket</label>
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search by ticket #, subject, requester…"
          />
          {pickerQ.data && pickerQ.data.items.length > 0 ? (
            <ul className="max-h-64 overflow-y-auto rounded-md border border-white/10 bg-white/[0.02]">
              {pickerQ.data.items.map((t) => (
                <li key={t.id}>
                  <button
                    type="button"
                    onClick={() => { setPicked(t); setResult(null); }}
                    className="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-white/[0.04]"
                  >
                    <span className="flex min-w-0 flex-1 items-center gap-2">
                      <span className="font-mono text-xs text-muted-foreground">#{t.number}</span>
                      <span className="truncate">{t.subject}</span>
                    </span>
                    <span className="flex items-center gap-2 text-xs text-muted-foreground">
                      <span
                        className="inline-block h-2 w-2 rounded-full"
                        style={{ backgroundColor: t.statusColor }}
                      />
                      {t.statusName}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          ) : pickerQ.isFetching ? (
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              <Loader2 className="h-3 w-3 animate-spin" />
              Searching…
            </div>
          ) : debouncedQuery.trim().length > 0 ? (
            <p className="text-xs text-muted-foreground/70">No tickets match.</p>
          ) : null}
        </div>
      ) : (
        <div className="flex items-center justify-between gap-3 rounded-md border border-white/10 bg-white/[0.04] px-3 py-2">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 text-sm">
              <span className="font-mono text-xs text-muted-foreground">#{picked.number}</span>
              <span className="truncate">{picked.subject}</span>
            </div>
            <div className="text-xs text-muted-foreground/80">
              {picked.requesterFirstName ?? ""} {picked.requesterLastName ?? ""}
              {picked.companyName ? ` · ${picked.companyName}` : ""}
            </div>
          </div>
          <Button variant="ghost" size="sm" onClick={reset}>
            Change
          </Button>
        </div>
      )}

      {/* Run button */}
      <div className="flex items-center justify-end gap-2">
        <Button
          type="button"
          onClick={() => runM.mutate()}
          disabled={!picked || runM.isPending}
          variant="secondary"
          className="gap-1.5"
        >
          {runM.isPending ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
          ) : (
            <Beaker className="h-3.5 w-3.5" />
          )}
          {runM.isPending ? "Running…" : "Run dry-run"}
        </Button>
      </div>

      {/* Result */}
      {runM.isError && (
        <div className="rounded-md border border-red-500/30 bg-red-500/[0.08] px-3 py-2 text-xs text-red-200/90">
          {runM.error instanceof Error ? runM.error.message : "Dry-run failed."}
        </div>
      )}

      {result && <DryRunDiff result={result} />}
    </div>
  );
}

function DryRunDiff({ result }: { result: TriggerDryRunResult }) {
  if (result.failureReason) {
    return (
      <div className="rounded-md border border-red-500/30 bg-red-500/[0.08] px-3 py-2 text-xs text-red-200/90">
        {result.failureReason}
      </div>
    );
  }

  if (!result.matched) {
    return (
      <div className="rounded-md border border-white/10 bg-white/[0.03] px-3 py-2 text-xs text-muted-foreground">
        <span className="inline-flex items-center gap-1.5">
          <MinusCircle className="h-3.5 w-3.5" />
          Conditions did not match — trigger would not fire on this ticket.
        </span>
      </div>
    );
  }

  if (result.actions.length === 0) {
    return (
      <div className="rounded-md border border-emerald-500/30 bg-emerald-500/[0.08] px-3 py-2 text-xs text-emerald-200/90">
        Conditions matched, but the trigger has no actions configured.
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <div className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
        Would apply
      </div>
      <ul className="space-y-2">
        {result.actions.map((a, i) => (
          <li key={i}>
            <ActionDiffCard action={a} />
          </li>
        ))}
      </ul>
    </div>
  );
}

function ActionDiffCard({ action }: { action: TriggerDryRunAction }) {
  const tone = toneFor(action.status);
  const Icon = iconFor(action.status);

  return (
    <div className={cn("rounded-md border px-3 py-2", tone.border, tone.bg)}>
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm">
          <Icon className={cn("h-4 w-4", tone.icon)} />
          <span className="font-mono text-xs">{action.kind}</span>
        </div>
        <span className={cn("text-[10px] font-medium uppercase tracking-wider", tone.label)}>
          {labelFor(action.status)}
        </span>
      </div>
      {action.failure && (
        <p className="mt-1.5 text-xs text-red-200/90">{action.failure}</p>
      )}
      {action.summary !== null && action.summary !== undefined && (
        <pre className="mt-2 max-h-48 overflow-auto rounded bg-black/30 px-2 py-1.5 font-mono text-[11px] text-muted-foreground/90">
          {JSON.stringify(action.summary, null, 2)}
        </pre>
      )}
    </div>
  );
}

function iconFor(status: TriggerDryRunAction["status"]) {
  switch (status) {
    case "wouldapply": return CheckCircle2;
    case "wouldnoop": return MinusCircle;
    case "failed": return XCircle;
    case "nohandler": return AlertCircle;
  }
}

function labelFor(status: TriggerDryRunAction["status"]) {
  switch (status) {
    case "wouldapply": return "Would apply";
    case "wouldnoop": return "No-op";
    case "failed": return "Failed";
    case "nohandler": return "No handler";
  }
}

function toneFor(status: TriggerDryRunAction["status"]) {
  switch (status) {
    case "wouldapply":
      return {
        border: "border-emerald-500/30",
        bg: "bg-emerald-500/[0.06]",
        icon: "text-emerald-300",
        label: "text-emerald-200/80",
      };
    case "wouldnoop":
      return {
        border: "border-white/10",
        bg: "bg-white/[0.03]",
        icon: "text-muted-foreground",
        label: "text-muted-foreground",
      };
    case "failed":
      return {
        border: "border-red-500/30",
        bg: "bg-red-500/[0.06]",
        icon: "text-red-300",
        label: "text-red-200/80",
      };
    case "nohandler":
      return {
        border: "border-amber-500/30",
        bg: "bg-amber-500/[0.06]",
        icon: "text-amber-300",
        label: "text-amber-200/80",
      };
  }
}

function useDebounced<T>(value: T, delay: number): T {
  const [v, setV] = React.useState(value);
  React.useEffect(() => {
    const id = setTimeout(() => setV(value), delay);
    return () => clearTimeout(id);
  }, [value, delay]);
  return v;
}
