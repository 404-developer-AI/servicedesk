import * as React from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlertTriangle,
  Building2,
  ChevronDown,
  ChevronRight,
  Download,
  MessageSquare,
  Plug,
  RefreshCw,
  Search,
} from "lucide-react";
import {
  apiErrorMessage,
  remoteDesktopApi,
  trmmAdminApi,
  type RdsStatus,
  type RemoteDesktopClient,
  type RemoteDesktopServer,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/auth/authStore";
import { toServerLocal, useServerTime } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";
import { ClientNotesSheet, type NotesTarget } from "./ClientNotesSheet";

const OVERVIEW_QK = ["assets", "remote-desktop", "overview"] as const;

function relativeTime(iso: string | null): string {
  if (!iso) return "never";
  const diffMs = Date.now() - new Date(iso).getTime();
  const sec = Math.round(diffMs / 1000);
  if (sec < 60) return `${sec}s ago`;
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} min ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const days = Math.round(hr / 24);
  return `${days}d ago`;
}

/// The script prints the server's own clock; the value arrives unzoned
/// ("yyyy-MM-ddTHH:mm:ss") and is shown verbatim as dd/MM/yyyy HH:mm.
function formatLocalStamp(value: string | null): string {
  if (!value) return "—";
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value);
  if (!m) return value;
  return `${m[3]}/${m[2]}/${m[1]} ${m[4]}:${m[5]}`;
}

function attentionMeta(status: RdsStatus | null): { label: string; tone: string } {
  switch (status) {
    case "failed":
      return { label: "Check failed", tone: "border-rose-400/40 bg-rose-500/15 text-rose-300" };
    case "no_check":
      return { label: "No check", tone: "border-amber-400/40 bg-amber-500/15 text-amber-300" };
    case "pending":
      return { label: "No result yet", tone: "border-sky-400/30 bg-sky-500/10 text-sky-300" };
    case "error":
      return { label: "Unreachable", tone: "border-glass-strong bg-glass-strong text-muted-foreground" };
    default:
      return { label: "Not checked", tone: "border-glass-strong bg-glass-strong text-muted-foreground" };
  }
}

/// Assets → Remote Desktop tab (v0.1.10). Groups every server whose RDS
/// script check reported TRUE under its TRMM client (code + name), hides
/// the FALSE ones, and lists failed / missing / pending checks separately
/// so they can be chased. Notes per client open in a side sheet.
export function RemoteDesktopTab({
  initialClientId,
  onClientConsumed,
}: {
  /// TRMM client id from the URL (global-search deep-link): opens that
  /// client's notes once, then the parent clears the param.
  initialClientId: number | null;
  onClientConsumed: () => void;
}) {
  const qc = useQueryClient();
  const { user } = useAuth();
  const isAdmin = user?.role === "Admin";
  const { time: serverTime } = useServerTime();
  const offsetMinutes = serverTime?.offsetMinutes ?? 0;

  const [search, setSearch] = React.useState("");
  const [debounced, setDebounced] = React.useState("");
  React.useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(search), 250);
    return () => window.clearTimeout(handle);
  }, [search]);

  const [notesTarget, setNotesTarget] = React.useState<NotesTarget | null>(null);
  const [expanded, setExpanded] = React.useState<Set<string>>(() => new Set());
  const toggleExpanded = (id: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const overview = useQuery({
    queryKey: [...OVERVIEW_QK, debounced] as const,
    queryFn: () => remoteDesktopApi.overview(debounced),
    staleTime: 5_000,
  });

  // Deep-link: open the notes of the client named in the URL as soon as
  // the overview knows its name/code. Consumed once.
  const consumedRef = React.useRef<number | null>(null);
  React.useEffect(() => {
    if (initialClientId === null || consumedRef.current === initialClientId) return;
    const data = overview.data;
    if (!data) return;
    const client =
      data.clients.find((c) => c.trmmClientId === initialClientId) ??
      (() => {
        const row = data.attention.find((s) => s.trmmClientId === initialClientId);
        return row
          ? {
              trmmClientId: row.trmmClientId,
              displayName: row.clientDisplayName,
              code: row.clientCode,
              companyName: row.companyName,
            }
          : null;
      })();
    consumedRef.current = initialClientId;
    if (client) setNotesTarget(client);
    onClientConsumed();
  }, [initialClientId, overview.data, onClientConsumed]);

  const triggerRdsSync = useMutation({
    mutationFn: () => trmmAdminApi.triggerRdsSync(),
    onSuccess: (result) => {
      if (result.success) {
        toast.success(
          `Checked ${result.agents} servers — ${result.rds} with Remote Desktop (${result.latencyMs} ms)`,
        );
        qc.invalidateQueries({ queryKey: ["assets", "remote-desktop"] });
      } else {
        toast.error(result.errorMessage ?? "Remote Desktop sync failed");
      }
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Remote Desktop sync failed"),
  });

  const data = overview.data;
  const disabled = data && !data.enabled;
  const neverSynced = data?.lastRdsSyncUtc == null;

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="max-w-2xl text-sm text-muted-foreground">
          Servers whose Remote Desktop check reports a session host, per client. Servers
          without Remote Desktop are hidden; failed or missing checks are listed below
          so they can be followed up.
        </p>
        <div className="flex items-center gap-2">
          <span className="text-xs text-muted-foreground">
            Last check sync:{" "}
            <strong className="text-foreground">{relativeTime(data?.lastRdsSyncUtc ?? null)}</strong>
            {data?.lastRdsStatus === "failed" && (
              <span className="ml-1 text-amber-400">— {data.lastRdsError ?? "failed"}</span>
            )}
          </span>
          {isAdmin && (
            <Button
              variant="outline"
              size="sm"
              onClick={() => triggerRdsSync.mutate()}
              disabled={!data?.enabled || triggerRdsSync.isPending}
              title="Re-read every server's check result from Tactical RMM now"
            >
              {triggerRdsSync.isPending ? (
                <>
                  <RefreshCw className="mr-1.5 h-3 w-3 animate-spin" /> Syncing…
                </>
              ) : (
                <>
                  <RefreshCw className="mr-1.5 h-3 w-3" /> Sync checks now
                </>
              )}
            </Button>
          )}
        </div>
      </div>

      {(disabled || (data && neverSynced)) && (
        <div className="rounded-lg border border-amber-400/30 bg-amber-500/[0.08] p-4 text-sm text-amber-200">
          <div className="flex items-start gap-3">
            <Plug className="mt-0.5 h-4 w-4 shrink-0" />
            <div className="flex-1">
              <p className="font-medium">
                {disabled
                  ? data?.trmmEnabled
                    ? "The Remote Desktop check sync is disabled."
                    : "Tactical RMM integration is disabled."
                  : "No Remote Desktop check sync has run yet."}
              </p>
              <p className="mt-1 text-xs text-amber-200/80">
                {disabled
                  ? "Enable it under Settings → Integrations → Tactical RMM → Remote Desktop check and set the script name."
                  : `The first sync runs shortly after start-up and then every ${data?.syncIntervalMinutes ?? 60} minutes.`}
              </p>
              {isAdmin && (
                <Link
                  to="/settings/integrations/trmm"
                  className="mt-2 inline-flex items-center gap-1 text-xs underline"
                >
                  Open TRMM settings →
                </Link>
              )}
            </div>
          </div>
        </div>
      )}

      <section className="rounded-lg border border-glass bg-glass p-4">
        <div className="flex flex-wrap items-center gap-2">
          <div className="relative min-w-[240px] flex-1">
            <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Client code, client name, hostname…"
              className="pl-7"
            />
          </div>
          <div className="flex flex-wrap items-center gap-1.5">
            <SummaryChip label="Clients" value={data?.summary.clients ?? 0} />
            <SummaryChip
              label="Remote Desktop servers"
              value={data?.summary.rdsServers ?? 0}
              tone="border-emerald-400/30 bg-emerald-500/10 text-emerald-300"
            />
            <SummaryChip
              label="Needs attention"
              value={data?.summary.attention ?? 0}
              tone={
                (data?.summary.attention ?? 0) > 0
                  ? "border-amber-400/40 bg-amber-500/15 text-amber-300"
                  : undefined
              }
            />
            <SummaryChip
              label="Hidden (no RDS)"
              value={data?.summary.hiddenNotRds ?? 0}
              title="Servers whose check reported FALSE — not shown by design"
            />
            {(data?.summary.unchecked ?? 0) > 0 && (
              <SummaryChip
                label="Not checked yet"
                value={data!.summary.unchecked}
                title="Servers the check sync has not visited yet"
              />
            )}
          </div>
          <a
            href={remoteDesktopApi.exportCsvUrl(debounced)}
            className={cn(
              "ml-auto inline-flex items-center gap-1 rounded-md border border-glass bg-glass px-2.5 py-1.5 text-xs text-foreground transition-colors hover:bg-glass-hover",
              (data?.summary.rdsServers ?? 0) + (data?.summary.attention ?? 0) === 0 &&
                "pointer-events-none opacity-40",
            )}
            title="Download the current list as CSV (opens in Excel)"
          >
            <Download className="h-3.5 w-3.5" />
            Export CSV
          </a>
        </div>
      </section>

      <section className="overflow-hidden rounded-lg border border-glass bg-glass">
        <table className="w-full text-sm">
          <thead className="bg-glass-strong/60 text-xs text-muted-foreground">
            <tr>
              <th className="w-8 px-2 py-2" />
              <th className="px-3 py-2 text-left">Server</th>
              <th className="px-3 py-2 text-left">OS</th>
              <th className="px-3 py-2 text-left">Last login</th>
              <th className="px-3 py-2 text-left">User</th>
              <th className="px-3 py-2 text-left">Checked</th>
              <th className="px-3 py-2 text-right">Notes</th>
            </tr>
          </thead>
          <tbody>
            {overview.isLoading ? (
              <tr>
                <td colSpan={7} className="p-3">
                  <Skeleton className="h-24 w-full" />
                </td>
              </tr>
            ) : (data?.clients.length ?? 0) === 0 ? (
              <tr>
                <td colSpan={7} className="px-3 py-10 text-center text-xs text-muted-foreground">
                  {debounced
                    ? "No Remote Desktop servers match the search."
                    : "No servers reported a Remote Desktop role yet."}
                </td>
              </tr>
            ) : (
              data!.clients.map((client) => (
                <ClientGroup
                  key={client.trmmClientId}
                  client={client}
                  expanded={expanded}
                  onToggle={toggleExpanded}
                  onOpenNotes={() => setNotesTarget(client)}
                  offsetMinutes={offsetMinutes}
                />
              ))
            )}
          </tbody>
        </table>
      </section>

      <section className="space-y-3">
        <div className="flex items-center gap-2">
          <AlertTriangle className="h-4 w-4 text-amber-400" />
          <h2 className="text-sm font-medium text-foreground">Needs attention</h2>
          <Badge className="border border-glass bg-glass-strong text-[10px] font-normal text-muted-foreground">
            {data?.attention.length ?? 0}
          </Badge>
          <span className="text-xs text-muted-foreground">
            Servers where the check failed, is missing, or has no result yet.
          </span>
        </div>
        <div className="overflow-hidden rounded-lg border border-glass bg-glass">
          <table className="w-full text-sm">
            <thead className="bg-glass-strong/60 text-xs text-muted-foreground">
              <tr>
                <th className="w-8 px-2 py-2" />
                <th className="px-3 py-2 text-left">Client</th>
                <th className="px-3 py-2 text-left">Server</th>
                <th className="px-3 py-2 text-left">OS</th>
                <th className="px-3 py-2 text-left">Reason</th>
                <th className="px-3 py-2 text-left">Checked</th>
                <th className="px-3 py-2 text-right">Notes</th>
              </tr>
            </thead>
            <tbody>
              {overview.isLoading ? (
                <tr>
                  <td colSpan={7} className="p-3">
                    <Skeleton className="h-12 w-full" />
                  </td>
                </tr>
              ) : (data?.attention.length ?? 0) === 0 ? (
                <tr>
                  <td colSpan={7} className="px-3 py-6 text-center text-xs text-muted-foreground">
                    Nothing to follow up — every checked server has a clean result.
                  </td>
                </tr>
              ) : (
                data!.attention.map((row) => (
                  <AttentionRow
                    key={row.id}
                    row={row}
                    open={expanded.has(row.id)}
                    onToggle={() => toggleExpanded(row.id)}
                    onOpenNotes={() =>
                      setNotesTarget({
                        trmmClientId: row.trmmClientId,
                        displayName: row.clientDisplayName,
                        code: row.clientCode,
                        companyName: row.companyName,
                      })
                    }
                    offsetMinutes={offsetMinutes}
                  />
                ))
              )}
            </tbody>
          </table>
        </div>
      </section>

      <ClientNotesSheet target={notesTarget} onClose={() => setNotesTarget(null)} />
    </div>
  );
}

function SummaryChip({
  label,
  value,
  tone,
  title,
}: {
  label: string;
  value: number;
  tone?: string;
  title?: string;
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-md border px-2 py-1 text-[11px]",
        tone ?? "border-glass bg-glass-strong text-muted-foreground",
      )}
      title={title}
    >
      <span className="font-semibold tabular-nums">{value}</span>
      {label}
    </span>
  );
}

function ClientCode({ code }: { code: string | null }) {
  if (!code) return null;
  return (
    <span className="rounded-md border border-primary/30 bg-primary/10 px-1.5 py-0.5 font-mono text-[11px] font-medium text-primary">
      {code}
    </span>
  );
}

function ClientGroup({
  client,
  expanded,
  onToggle,
  onOpenNotes,
  offsetMinutes,
}: {
  client: RemoteDesktopClient;
  expanded: Set<string>;
  onToggle: (id: string) => void;
  onOpenNotes: () => void;
  offsetMinutes: number;
}) {
  return (
    <>
      <tr className="border-t border-glass bg-glass-strong/40">
        <td className="px-2 py-2" />
        <td colSpan={5} className="px-3 py-2">
          <div className="flex flex-wrap items-center gap-2">
            <ClientCode code={client.code} />
            <span className="font-medium text-foreground">{client.displayName}</span>
            {client.companyId && client.companyName && (
              <Link
                to="/companies/$companyId"
                params={{ companyId: client.companyId }}
                className="inline-flex items-center gap-1 text-[11px] text-muted-foreground transition-colors hover:text-foreground"
                title="Open the linked company"
              >
                <Building2 className="h-3 w-3" />
                {client.companyName}
              </Link>
            )}
            <span className="text-[11px] text-muted-foreground">
              {client.servers.length === 1 ? "1 server" : `${client.servers.length} servers`}
            </span>
          </div>
        </td>
        <td className="px-3 py-2 text-right">
          <NotesButton count={client.noteCount} onClick={onOpenNotes} />
        </td>
      </tr>
      {client.servers.map((s) => (
        <ServerRow
          key={s.id}
          row={s}
          open={expanded.has(s.id)}
          onToggle={() => onToggle(s.id)}
          offsetMinutes={offsetMinutes}
        />
      ))}
    </>
  );
}

function NotesButton({ count, onClick }: { count: number; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-md border px-2 py-1 text-[11px] transition-colors",
        count > 0
          ? "border-primary/30 bg-primary/10 text-primary hover:bg-primary/15"
          : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
      )}
      title={count > 0 ? `${count} note${count === 1 ? "" : "s"}` : "Add a note"}
    >
      <MessageSquare className="h-3.5 w-3.5" />
      {count > 0 ? count : "Note"}
    </button>
  );
}

function OsCell({ row }: { row: RemoteDesktopServer }) {
  return (
    <div className="flex flex-col">
      <span className="text-xs text-foreground">{row.osFamily ?? row.osName ?? "—"}</span>
      {row.osBuild && row.osFamily && (
        <span className="text-[10px] text-muted-foreground">{row.osBuild}</span>
      )}
    </div>
  );
}

function ExpandCell({ open, onToggle }: { open: boolean; onToggle: () => void }) {
  return (
    <td className="px-2 py-2 align-top">
      <button
        type="button"
        onClick={onToggle}
        className="rounded p-0.5 text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
        title={open ? "Hide script output" : "Show script output"}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
      </button>
    </td>
  );
}

function OutputPanel({ row, colSpan }: { row: RemoteDesktopServer; colSpan: number }) {
  const hasAny = row.stdout || row.stderr || row.fetchError;
  return (
    <tr className="border-t border-glass/60 bg-glass-strong/20">
      <td />
      <td colSpan={colSpan} className="px-3 pb-3 pt-1">
        {!hasAny ? (
          <p className="text-xs text-muted-foreground">No script output recorded.</p>
        ) : (
          <div className="space-y-2">
            {row.stdout && (
              <pre className="max-h-64 overflow-auto rounded-md border border-glass bg-glass p-2 font-mono text-[11px] leading-relaxed text-foreground whitespace-pre-wrap">
                {row.stdout}
              </pre>
            )}
            {row.stderr && (
              <pre className="max-h-40 overflow-auto rounded-md border border-rose-400/30 bg-rose-500/10 p-2 font-mono text-[11px] leading-relaxed text-rose-200 whitespace-pre-wrap">
                {row.stderr}
              </pre>
            )}
            {row.fetchError && (
              <p className="text-xs text-amber-300">Could not read this agent's checks: {row.fetchError}</p>
            )}
            <p className="text-[10px] text-muted-foreground">
              {row.checkStatus && <>Check status: {row.checkStatus}. </>}
              {row.retcode !== null && <>Exit code {row.retcode}. </>}
              Site: {row.siteName}.
            </p>
          </div>
        )}
      </td>
    </tr>
  );
}

function ServerRow({
  row,
  open,
  onToggle,
  offsetMinutes,
}: {
  row: RemoteDesktopServer;
  open: boolean;
  onToggle: () => void;
  offsetMinutes: number;
}) {
  return (
    <>
      <tr className="border-t border-glass transition-colors hover:bg-glass-strong/40">
        <ExpandCell open={open} onToggle={onToggle} />
        <td className="px-3 py-2 font-medium text-foreground">
          <div className="flex items-center gap-2">
            <span
              className={cn(
                "inline-block h-1.5 w-1.5 rounded-full",
                row.online ? "bg-emerald-400" : "bg-glass-strong",
              )}
              title={row.online ? "Online" : `Offline · last seen ${relativeTime(row.lastSeenUtc)}`}
            />
            {row.hostname}
          </div>
        </td>
        <td className="px-3 py-2">
          <OsCell row={row} />
        </td>
        <td className="px-3 py-2 text-foreground">
          <div className="flex items-center gap-1.5">
            <span className="text-xs tabular-nums">{formatLocalStamp(row.lastLoginLocal)}</span>
            {row.lastLoginKind && (
              <Badge
                className={cn(
                  "border text-[10px] font-normal uppercase",
                  row.lastLoginKind === "rdp"
                    ? "border-sky-400/30 bg-sky-500/10 text-sky-300"
                    : "border-glass bg-glass-strong text-muted-foreground",
                )}
              >
                {row.lastLoginKind}
              </Badge>
            )}
          </div>
        </td>
        <td className="px-3 py-2 text-xs text-muted-foreground">{row.lastLoginUser ?? "—"}</td>
        <td
          className="px-3 py-2 text-xs text-muted-foreground"
          title={row.lastRunUtc ? toServerLocal(row.lastRunUtc, offsetMinutes, true) : undefined}
        >
          {relativeTime(row.lastRunUtc)}
        </td>
        <td className="px-3 py-2" />
      </tr>
      {open && <OutputPanel row={row} colSpan={6} />}
    </>
  );
}

function AttentionRow({
  row,
  open,
  onToggle,
  onOpenNotes,
  offsetMinutes,
}: {
  row: RemoteDesktopServer;
  open: boolean;
  onToggle: () => void;
  onOpenNotes: () => void;
  offsetMinutes: number;
}) {
  const meta = attentionMeta(row.status);
  return (
    <>
      <tr className="border-t border-glass transition-colors hover:bg-glass-strong/40">
        <ExpandCell open={open} onToggle={onToggle} />
        <td className="px-3 py-2">
          <div className="flex items-center gap-2">
            <ClientCode code={row.clientCode} />
            <span className="text-xs text-foreground">{row.clientDisplayName}</span>
          </div>
        </td>
        <td className="px-3 py-2 font-medium text-foreground">
          <div className="flex items-center gap-2">
            <span
              className={cn(
                "inline-block h-1.5 w-1.5 rounded-full",
                row.online ? "bg-emerald-400" : "bg-glass-strong",
              )}
              title={row.online ? "Online" : `Offline · last seen ${relativeTime(row.lastSeenUtc)}`}
            />
            {row.hostname}
          </div>
        </td>
        <td className="px-3 py-2">
          <OsCell row={row} />
        </td>
        <td className="px-3 py-2">
          <Badge className={cn("border text-[10px] font-normal", meta.tone)}>{meta.label}</Badge>
        </td>
        <td
          className="px-3 py-2 text-xs text-muted-foreground"
          title={row.lastRunUtc ? toServerLocal(row.lastRunUtc, offsetMinutes, true) : undefined}
        >
          {relativeTime(row.lastRunUtc)}
        </td>
        <td className="px-3 py-2 text-right">
          <button
            type="button"
            onClick={onOpenNotes}
            className="inline-flex items-center gap-1 rounded-md border border-glass bg-glass px-2 py-1 text-[11px] text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
            title="Open this client's notes"
          >
            <MessageSquare className="h-3.5 w-3.5" />
          </button>
        </td>
      </tr>
      {open && <OutputPanel row={row} colSpan={6} />}
    </>
  );
}
