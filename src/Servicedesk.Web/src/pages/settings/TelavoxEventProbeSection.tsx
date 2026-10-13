import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { AlertCircle, CheckCircle2, Copy, FlaskConical, KeyRound, RefreshCw, Trash2 } from "lucide-react";
import { ApiError, telavoxAdminApi, type SettingEntry, type TelavoxProbeEvent } from "@/lib/api";
import { formatDateTime } from "@/lib/dateFormat";
import { Button } from "@/components/ui/button";
import { SettingField } from "@/components/settings/SettingField";
import { cn } from "@/lib/utils";

const PROBE_QK = ["integrations", "telavox", "event-probe"] as const;

/// The query string the guide asks for on each Telavox webhook; every value
/// is a Telavox data parameter.
const PROBE_QUERY =
  "?event={system.event}&caller={system.caller}&callee={system.callee}&direction={system.direction}&agent={system.email}&ts={system.unixtimestamp}";

/// Settings → Integrations → Telavox → Call events (probe), v0.1.36. Shows
/// what Telavox webhooks actually send before the push-based popup is
/// built. Nothing acts on these events; the last 50 live in memory only.
export function TelavoxEventProbeSection({
  enabledEntry,
  settingsQueryKey,
}: {
  enabledEntry: SettingEntry | undefined;
  settingsQueryKey: readonly unknown[];
}) {
  const qc = useQueryClient();
  const probe = useQuery({
    queryKey: PROBE_QK,
    queryFn: telavoxAdminApi.eventProbe,
    refetchInterval: (q) => (q.state.data?.enabled ? 10_000 : false),
  });
  const [freshToken, setFreshToken] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<"rotate" | "delete" | null>(null);

  const configured = probe.data?.tokenConfigured ?? false;
  const url = `${window.location.origin}${probe.data?.path ?? "/api/public/telavox/call-events"}${PROBE_QUERY}`;

  const generate = useMutation({
    mutationFn: () => telavoxAdminApi.generateEventProbeToken(),
    onSuccess: (r) => {
      setFreshToken(r.token);
      setConfirm(null);
      toast.success(configured ? "New probe token generated — the old one no longer works" : "Probe token generated");
      qc.invalidateQueries({ queryKey: PROBE_QK });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Could not generate the token (${err.status})` : "Could not generate the token"),
  });
  const remove = useMutation({
    mutationFn: () => telavoxAdminApi.deleteEventProbeToken(),
    onSuccess: () => {
      setFreshToken(null);
      setConfirm(null);
      toast.success("Probe token deleted");
      qc.invalidateQueries({ queryKey: PROBE_QK });
    },
  });
  const clear = useMutation({
    mutationFn: () => telavoxAdminApi.clearEventProbe(),
    onSuccess: () => qc.invalidateQueries({ queryKey: PROBE_QK }),
  });

  const copy = async (value: string, label: string) => {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(`${label} copied`);
    } catch {
      toast.error("Could not copy — select the value manually");
    }
  };

  const events = probe.data?.events ?? [];

  return (
    <section className="sd-telavox-probe space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
      <div className="flex items-center gap-2 text-sm font-medium text-foreground">
        <FlaskConical className="h-4 w-4 text-muted-foreground" />
        Call events (probe)
      </div>
      <p className="max-w-2xl text-xs text-muted-foreground/70">
        A test receiver for Telavox call-event webhooks (ringing, answer, hangup). It only shows what
        Telavox sends, so the call popup can later move from polling to push. Nothing acts on these
        events; the last 50 are kept in memory until a restart, with phone numbers masked.
      </p>

      {enabledEntry ? (
        <SettingField
          entry={enabledEntry}
          queryKey={settingsQueryKey}
          label="Enabled"
          hint="Off, or no token generated: the probe URL answers as if it doesn't exist."
        />
      ) : null}

      <div className="space-y-2">
        <div className="flex items-center gap-2 text-xs font-medium text-foreground">
          <KeyRound className="h-3.5 w-3.5 text-muted-foreground" />
          Token
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {!configured ? (
            <Button size="sm" className="h-9" disabled={generate.isPending} onClick={() => generate.mutate()}>
              Generate token
            </Button>
          ) : confirm === null ? (
            <>
              <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
                <CheckCircle2 className="h-3 w-3" /> Token set
              </span>
              <Button size="sm" variant="ghost" className="h-9" onClick={() => setConfirm("rotate")}>
                Generate new token
              </Button>
              <Button
                size="sm"
                variant="ghost"
                className="h-9 text-muted-foreground hover:text-foreground"
                onClick={() => setConfirm("delete")}
              >
                <Trash2 className="mr-1.5 h-3.5 w-3.5" />
                Delete
              </Button>
            </>
          ) : (
            <>
              <span className="text-xs text-amber-300">
                {confirm === "rotate"
                  ? "The current token stops working at once — update the Telavox webhooks afterwards."
                  : "The probe stops receiving at once."}
              </span>
              <Button
                size="sm"
                className="h-9"
                disabled={generate.isPending || remove.isPending}
                onClick={() => (confirm === "rotate" ? generate.mutate() : remove.mutate())}
              >
                {confirm === "rotate" ? "Generate new token" : "Delete token"}
              </Button>
              <Button size="sm" variant="ghost" className="h-9" onClick={() => setConfirm(null)}>
                Cancel
              </Button>
            </>
          )}
          {!configured && (
            <span className="inline-flex items-center gap-1 text-xs text-muted-foreground/60">
              <AlertCircle className="h-3 w-3" /> No token yet
            </span>
          )}
        </div>
        {freshToken && (
          <div className="space-y-1.5 rounded-lg border border-amber-400/30 bg-amber-500/8 p-3">
            <p className="text-xs text-amber-200">
              Copy the token now — it is shown only once. It is not the contact-lookup token.
            </p>
            <div className="flex items-center gap-2">
              <code className="min-w-0 flex-1 break-all rounded bg-glass px-2 py-1 font-mono text-xs text-foreground">
                Bearer {freshToken}
              </code>
              <Button size="sm" variant="ghost" className="h-8" onClick={() => copy(`Bearer ${freshToken}`, "Header value")}>
                <Copy className="h-3.5 w-3.5" />
              </Button>
            </div>
          </div>
        )}
      </div>

      <div className="space-y-1.5">
        <div className="text-xs font-medium text-foreground">Telavox webhooks (one per event: ringing, answer, hangup)</div>
        <dl className="grid grid-cols-[max-content_1fr] items-center gap-x-4 gap-y-1.5 text-xs">
          <dt className="text-muted-foreground">Method</dt>
          <dd className="text-foreground">POST</dd>
          <dt className="text-muted-foreground">URL</dt>
          <dd className="flex min-w-0 items-center gap-2">
            <code className="min-w-0 break-all font-mono text-foreground">{url}</code>
            <Button size="sm" variant="ghost" className="h-7 shrink-0" onClick={() => copy(url, "URL")}>
              <Copy className="h-3.5 w-3.5" />
            </Button>
          </dd>
          <dt className="text-muted-foreground">Header</dt>
          <dd className="font-mono text-foreground">Authorization: Bearer &lt;probe token&gt;</dd>
          <dt className="text-muted-foreground">Return variable</dt>
          <dd className="text-foreground">Not needed</dd>
        </dl>
      </div>

      <div className="space-y-2">
        <div className="flex items-center justify-between gap-2">
          <div className="text-xs font-medium text-foreground">
            Received {probe.data ? `(${probe.data.received} since the last restart)` : ""}
          </div>
          <div className="flex items-center gap-1">
            <Button size="sm" variant="ghost" className="h-8" onClick={() => probe.refetch()}>
              <RefreshCw className={cn("mr-1.5 h-3.5 w-3.5", probe.isFetching && "animate-spin")} />
              Refresh
            </Button>
            <Button
              size="sm"
              variant="ghost"
              className="h-8 text-muted-foreground hover:text-foreground"
              disabled={events.length === 0 || clear.isPending}
              onClick={() => clear.mutate()}
            >
              <Trash2 className="mr-1.5 h-3.5 w-3.5" />
              Clear
            </Button>
          </div>
        </div>
        {events.length === 0 ? (
          <p className="rounded-lg border border-glass bg-glass px-3 py-4 text-center text-xs text-muted-foreground">
            Nothing received yet. Make a test call to a user the webhooks are assigned to.
          </p>
        ) : (
          <ol className="space-y-2">
            {events.map((e, i) => (
              <ProbeEventRow key={`${e.receivedUtc}-${i}`} event={e} />
            ))}
          </ol>
        )}
      </div>
    </section>
  );
}

function ProbeEventRow({ event }: { event: TelavoxProbeEvent }) {
  return (
    <li className="space-y-1.5 rounded-lg border border-glass bg-glass px-3 py-2 text-xs">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="font-mono text-foreground">{formatDateTime(event.receivedUtc, null, true)}</span>
        <span className="rounded border border-glass px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
          {event.method}
        </span>
        {event.contentType ? <span className="text-muted-foreground">{event.contentType}</span> : null}
        {event.agentEmail ? (
          <span className={cn("ml-auto", event.agentMatchesUser ? "text-emerald-300" : "text-amber-300")}>
            {event.agentMatchesUser ? "Agent matches a user" : "Agent e-mail matches no active user"}
          </span>
        ) : null}
      </div>
      <div className="flex flex-wrap gap-1.5">
        {event.fields.map((f, i) => (
          <span key={i} className="rounded border border-glass bg-glass px-1.5 py-0.5 font-mono text-[11px]">
            <span className="text-muted-foreground">{f.name}</span>
            <span className="text-muted-foreground/60">=</span>
            <span className="text-foreground">{f.value || "—"}</span>
          </span>
        ))}
      </div>
    </li>
  );
}
