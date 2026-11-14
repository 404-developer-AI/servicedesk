import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { CheckCircle2, ClipboardList, Hand, Inbox, Layers, Search, X } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { AgentPicker } from "@/components/AgentPicker";
import { CompanyPicker } from "@/components/CompanyPicker";
import { useDebounced } from "@/hooks/useDebounced";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { formatNumber, formatTimeAgo } from "@/lib/format";
import { cn } from "@/lib/utils";
import {
  backofficeFormKeys,
  backofficeFormsApi,
  type BackofficeFormItem,
  type BackofficeFormTab,
} from "@/lib/backoffice-forms-api";
import { BackofficeFormDialog } from "./BackofficeFormDialog";
import { BackofficeStatusChip } from "./BackofficeStatusChip";

const PAGE_SIZE = 50;

const TABS: { id: BackofficeFormTab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: "Open", label: "Open", icon: Inbox },
  { id: "InProgress", label: "In progress", icon: Hand },
  { id: "Handled", label: "Handled", icon: CheckCircle2 },
  { id: "All", label: "All", icon: Layers },
];

const EMPTY_TEXT: Record<BackofficeFormTab, string> = {
  Open: "Nothing waiting. New forms appear here as soon as a technician submits one.",
  InProgress: "Nobody is handling a form right now.",
  Handled: "No handled forms yet.",
  All: "No back office forms yet. Mark a form template with “Send to back office” to start.",
};

/// v0.1.54 — the Back office forms work list. Every form filled from a
/// template marked "Send to back office"; flag-gated route and API. Open and
/// In progress list oldest first (a queue to work through), Handled and All
/// newest first. `?form=<id>` opens a form directly (search, notifications).
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function formIdFromUrl(): string | null {
  const id = new URLSearchParams(window.location.search).get("form");
  return id && UUID_RE.test(id) ? id : null;
}

export function BackofficeFormsPage() {
  const zone = useServerTimeZone();
  const [tab, setTab] = React.useState<BackofficeFormTab>("Open");
  const [search, setSearch] = React.useState("");
  const [templateId, setTemplateId] = React.useState<string>("");
  const [companyId, setCompanyId] = React.useState<string | null>(null);
  const [submittedBy, setSubmittedBy] = React.useState<string | null>(null);
  const [page, setPage] = React.useState(1);
  const [openId, setOpenId] = React.useState<string | null>(formIdFromUrl);
  const q = useDebounced(search, 250);

  React.useEffect(() => setPage(1), [tab, q, templateId, companyId, submittedBy]);

  const params = {
    status: tab, q, templateId: templateId || null, companyId, submittedBy,
    offset: (page - 1) * PAGE_SIZE, limit: PAGE_SIZE,
  };
  const list = useQuery({
    queryKey: backofficeFormKeys.list(params),
    queryFn: () => backofficeFormsApi.list(params),
    placeholderData: keepPreviousData,
    staleTime: 15_000,
  });
  const counts = useQuery({
    queryKey: backofficeFormKeys.counts(),
    queryFn: backofficeFormsApi.counts,
    staleTime: 30_000,
    refetchOnWindowFocus: true,
  });
  const templates = useQuery({
    queryKey: backofficeFormKeys.templates(),
    queryFn: backofficeFormsApi.templates,
    staleTime: 5 * 60_000,
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const items = list.data?.items ?? [];
  const filtered = !!(q.trim() || templateId || companyId || submittedBy);
  const countFor = (t: BackofficeFormTab) =>
    t === "Open" ? counts.data?.open : t === "InProgress" ? counts.data?.inProgress : undefined;

  return (
    <div className="sd-bo-forms flex min-h-0 w-full flex-1 flex-col gap-6">
      <header className="space-y-1">
        <h1 className="text-display-md font-semibold text-foreground">Back office forms</h1>
        <p className="text-sm text-muted-foreground">
          Forms that technicians filled in on tickets for back office follow-up — licence requests, activations,
          contract checks. Pick one up, handle it and leave a note of what was done.
        </p>
      </header>

      <div className="flex flex-wrap items-center gap-3">
        <div className="glass-panel flex items-center gap-1 p-1" role="tablist" aria-label="Status">
          {TABS.map((t) => {
            const Icon = t.icon;
            const n = countFor(t.id);
            return (
              <button key={t.id} type="button" role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}
                className={cn(
                  "relative inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-colors",
                  tab === t.id
                    ? "bg-glass-strong text-foreground shadow-[inset_0_0_0_1px_hsl(var(--border))]"
                    : "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                )}>
                <Icon className="h-3.5 w-3.5" />
                {t.label}
                {n !== undefined && n > 0 && (
                  <span className="rounded-md bg-glass-strong px-1.5 text-[11px] tabular-nums text-muted-foreground">{formatNumber(n)}</span>
                )}
                {t.id === "Open" && (n ?? 0) > 0 && (
                  <span className="absolute right-1 top-1 h-1.5 w-1.5 rounded-full bg-red-500" />
                )}
              </button>
            );
          })}
        </div>

        <div className="relative min-w-56 flex-1 sm:max-w-xs">
          <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input value={search} onChange={(e) => setSearch(e.target.value)}
            placeholder="Search form, answers, ticket # or company…" className="pl-9" />
        </div>
        <select value={templateId} onChange={(e) => setTemplateId(e.target.value)} aria-label="Form"
          className="h-9 rounded-md border border-glass bg-glass px-2.5 text-sm text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring [&_option]:bg-popover [&_option]:text-popover-foreground">
          <option value="">All forms</option>
          {(templates.data ?? []).map((t) => (
            <option key={t.id} value={t.id}>{t.name}{t.isActive ? "" : " (inactive)"}</option>
          ))}
        </select>
        <div className="w-52"><CompanyPicker value={companyId} onChange={setCompanyId} placeholder="Any company" /></div>
        <div className="w-48"><AgentPicker value={submittedBy} onChange={setSubmittedBy} placeholder="Any technician" /></div>
        {filtered && (
          <Button variant="ghost" size="sm" onClick={() => { setSearch(""); setTemplateId(""); setCompanyId(null); setSubmittedBy(null); }}>
            <X className="mr-1 h-3.5 w-3.5" /> Clear
          </Button>
        )}
      </div>

      <div className="glass-panel flex-1 overflow-hidden">
        {list.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : list.isError ? (
          <div className="p-6 text-sm text-rose-600 dark:text-rose-300">Could not load the back office forms. Refresh to try again.</div>
        ) : items.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <ClipboardList className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">{filtered ? "No forms match these filters." : EMPTY_TEXT[tab]}</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                  <th className="px-4 py-2.5 font-medium">Form</th>
                  <th className="px-3 py-2.5 font-medium">Ticket</th>
                  <th className="px-3 py-2.5 font-medium">Company</th>
                  <th className="px-3 py-2.5 font-medium">Submitted</th>
                  <th className="px-4 py-2.5 font-medium">Status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-glass">
                {items.map((f) => (
                  <Row key={f.id} item={f} zone={zone} onOpen={() => setOpenId(f.id)} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>Page {page} of {totalPages} · {formatNumber(total)} forms</span>
          <div className="flex items-center gap-2">
            <Button size="sm" variant="ghost" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page <= 1 || list.isFetching}>
              Previous
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setPage((p) => Math.min(totalPages, p + 1))} disabled={page >= totalPages || list.isFetching}>
              Next
            </Button>
          </div>
        </div>
      )}

      <BackofficeFormDialog formId={openId} onOpenChange={(o) => !o && setOpenId(null)} />
    </div>
  );
}

function Row({ item, zone, onOpen }: { item: BackofficeFormItem; zone: string | null; onOpen: () => void }) {
  return (
    <tr className="sd-bo-row cursor-pointer transition-colors hover:bg-glass-hover" onClick={onOpen}
      onKeyDown={(e) => { if (e.key === "Enter") onOpen(); }} tabIndex={0}>
      <td className="px-4 py-2.5">
        <div className="flex items-center gap-2">
          <span className="font-medium text-foreground">{item.name}</span>
          {item.reopened && (
            <span className="rounded-md border border-amber-400/45 bg-amber-100/70 px-1.5 py-px text-[10px] font-medium text-amber-900 dark:bg-amber-400/12 dark:text-amber-200">
              Changed after handling
            </span>
          )}
          {item.updatedSinceClaim && (
            <span className="rounded-md border border-glass-strong bg-glass px-1.5 py-px text-[10px] font-medium text-muted-foreground">Updated</span>
          )}
        </div>
        {item.version > 1 && <div className="text-[11px] tabular-nums text-muted-foreground/70">v{item.version}</div>}
      </td>
      <td className="max-w-72 px-3 py-2.5">
        <div className="truncate">
          <span className="font-mono text-xs tabular-nums text-muted-foreground">#{item.ticketNumber}</span>{" "}
          <span className="text-foreground">{item.ticketSubject}</span>
        </div>
      </td>
      <td className="max-w-48 truncate px-3 py-2.5 text-muted-foreground">{item.companyName ?? "—"}</td>
      <td className="whitespace-nowrap px-3 py-2.5">
        <div className="text-foreground">{item.submittedByName ?? "unknown"}</div>
        <div className="text-[11px] text-muted-foreground/80" title={formatDateTime(item.submittedUtc, zone)}>
          {formatTimeAgo(item.submittedUtc, { dateAfterDays: 7 })}
        </div>
      </td>
      <td className="px-4 py-2.5"><BackofficeStatusChip item={item} /></td>
    </tr>
  );
}
