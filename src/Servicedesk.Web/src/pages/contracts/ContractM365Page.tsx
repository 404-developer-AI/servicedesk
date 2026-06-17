import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Link, useNavigate } from "@tanstack/react-router";
import {
  Building2,
  Check,
  ChevronLeft,
  ChevronRight,
  Plug,
  RefreshCw,
  Search,
  Settings2,
  X,
} from "lucide-react";
import {
  articlesApi,
  contractM365Api,
  type M365Company,
  type M365Connection,
  type M365LinkStatus,
  type M365SelectedArticle,
} from "@/lib/api";
import { M365StatusBadge, openConsentWindow } from "@/components/contracts/m365Connection";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";

const COMPANIES_KEY = ["contracts", "m365", "companies"] as const;
const CONNECTIONS_KEY = ["contracts", "m365", "connections"] as const;
const SELECTION_KEY = ["contracts", "m365", "selection"] as const;

/// Contracts → Microsoft 365 matching. Mounted only when the user has the
/// `contracts_enabled` feature flag (route gate + backend RequireAgent +
/// in-handler flag check). Lists the companies whose contracts reference any
/// of the curated "M365-related" Adsolut articles — each company once. The
/// gear (top-right) opens a dialog to pick which articles those are; the
/// "Connect with M365" button per company is intentionally a no-op for now.
type ConnFilter = "all" | "connected" | "not-connected";

export function ContractM365Page() {
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [search, setSearch] = useState("");
  const [connFilter, setConnFilter] = useState<ConnFilter>("all");

  const companies = useQuery({
    queryKey: COMPANIES_KEY,
    queryFn: () => contractM365Api.companies(),
  });

  // Connection status per company. Refetches on window focus so the badge
  // updates when the agent returns from the consent tab.
  const connections = useQuery({
    queryKey: CONNECTIONS_KEY,
    queryFn: () => contractM365Api.connections(),
    refetchOnWindowFocus: true,
    staleTime: 10_000,
  });

  const connectionMap = useMemo(() => {
    const map = new Map<string, M365Connection>();
    for (const c of connections.data?.items ?? []) map.set(c.companyId, c);
    return map;
  }, [connections.data]);

  const items = companies.data?.items ?? [];
  const total = items.length;
  const connectedCount = (connections.data?.items ?? []).filter(
    (c) => c.status === "connected",
  ).length;

  // Name/code search + M365 connection filter, both client-side over the
  // already-loaded list (the matching set is bounded — one row per company).
  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return items.filter((c) => {
      if (needle) {
        const hay = `${c.name ?? ""} ${c.code ?? ""}`.toLowerCase();
        if (!hay.includes(needle)) return false;
      }
      if (connFilter !== "all") {
        const isConnected = connectionMap.get(c.companyId)?.status === "connected";
        if (connFilter === "connected" && !isConnected) return false;
        if (connFilter === "not-connected" && isConnected) return false;
      }
      return true;
    });
  }, [items, search, connFilter, connectionMap]);

  return (
    <div className="flex flex-1 flex-col gap-4 p-4 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <Link
            to="/contracts"
            className="flex h-10 w-10 items-center justify-center rounded-full border border-glass bg-glass text-muted-foreground transition-colors hover:text-foreground"
            aria-label="Back to Contracts"
          >
            <ChevronLeft className="h-4.5 w-4.5" />
          </Link>
          <div>
            <h1 className="text-display-md font-semibold text-foreground">Microsoft 365 matching</h1>
            <p className="text-xs text-muted-foreground">
              {total.toLocaleString()} {total === 1 ? "company" : "companies"} with a matching contract article
              {connectedCount > 0 && (
                <span className="text-muted-foreground/70"> · {connectedCount} connected</span>
              )}
            </p>
          </div>
        </div>
        <Button
          size="sm"
          variant="ghost"
          className="gap-1.5"
          onClick={() => setSettingsOpen(true)}
          aria-label="Configure M365 articles"
        >
          <Settings2 className="h-4 w-4" />
          Configure articles
        </Button>
      </div>

      {!companies.isLoading && !companies.isError && items.length > 0 && (
        <div className="flex flex-wrap items-center gap-2">
          <div className="relative min-w-[14rem] flex-1 sm:max-w-sm">
            <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search name or code…"
              className="pl-9"
            />
          </div>
          <div className="flex items-center gap-1 rounded-full border border-glass bg-glass p-0.5">
            {([
              { key: "all", label: "All" },
              { key: "connected", label: "Connected" },
              { key: "not-connected", label: "Not connected" },
            ] as { key: ConnFilter; label: string }[]).map((opt) => (
              <button
                key={opt.key}
                type="button"
                onClick={() => setConnFilter(opt.key)}
                className={cn(
                  "rounded-full px-3 py-1 text-xs font-medium transition-colors",
                  connFilter === opt.key
                    ? "bg-glass-strong text-foreground"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {opt.label}
              </button>
            ))}
          </div>
        </div>
      )}

      <div className="glass-panel flex-1 overflow-hidden">
        {companies.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : companies.isError ? (
          <div className="p-6 text-sm text-rose-300">
            Could not load companies. Refresh or check the integration status.
          </div>
        ) : items.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-3 p-12 text-center">
            <Building2 className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">
              No companies yet. Pick the Microsoft 365-related contract articles below — and make
              sure a contracts sync has run, which links each contract to its company.
            </p>
            <Button size="sm" variant="ghost" className="gap-1.5" onClick={() => setSettingsOpen(true)}>
              <Settings2 className="h-4 w-4" />
              Configure articles
            </Button>
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-3 p-12 text-center">
            <Search className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">
              No companies match your search or filter.
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                  <th className="px-3 py-2.5 font-medium">Customer code</th>
                  <th className="px-3 py-2.5 font-medium">Customer name</th>
                  <th className="w-72 px-3 py-2.5 font-medium">Microsoft 365</th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((c) => (
                  <CompanyRow
                    key={c.companyId}
                    company={c}
                    connection={connectionMap.get(c.companyId)}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <ArticleSelectionDialog open={settingsOpen} onOpenChange={setSettingsOpen} />
    </div>
  );
}

// ---- company row ------------------------------------------------------

function CompanyRow({
  company,
  connection,
}: {
  company: M365Company;
  connection: M365Connection | undefined;
}) {
  const qc = useQueryClient();
  const navigate = useNavigate();
  const status: M365LinkStatus | null = connection?.status ?? null;
  const connected = status === "connected";
  const needsConsent = status === null || status === "disconnected" || status === "needs_reconsent";

  const connect = useMutation({
    mutationFn: () => openConsentWindow(() => contractM365Api.connect(company.companyId)),
    onError: () =>
      toast.error(
        "Could not start the Microsoft 365 connection. Check that the integration is configured under Settings → Integrations.",
      ),
  });

  const sync = useMutation({
    mutationFn: () => contractM365Api.sync(company.companyId),
    onSuccess: (r) => {
      if (r.success) toast.success(r.changed ? `Synced — ${r.mailboxCount} mailboxes updated.` : "Synced — no changes.");
      else toast.error(r.error ?? "Sync failed.");
      qc.invalidateQueries({ queryKey: CONNECTIONS_KEY });
    },
    onError: () => toast.error("Sync failed."),
  });

  const open = () => navigate({ to: "/contracts/m365-matching/$companyId", params: { companyId: company.companyId } });

  return (
    <tr className="border-b border-glass transition-colors hover:bg-glass-hover">
      <td className="px-3 py-2.5">
        <span className="font-mono text-xs text-foreground">{company.code ?? "—"}</span>
      </td>
      <td className="px-3 py-2.5">
        <button
          type="button"
          onClick={open}
          className="text-left text-foreground transition-colors hover:text-primary"
        >
          {company.name || "—"}
        </button>
      </td>
      <td className="px-3 py-2.5">
        <div className="flex items-center justify-end gap-2">
          {connected && (
            <button
              type="button"
              onClick={open}
              className="flex items-center gap-1.5 text-xs text-muted-foreground transition-colors hover:text-foreground"
            >
              <M365StatusBadge status={status} />
              <span className="tabular-nums">{connection?.mailboxCount ?? 0}</span>
              <ChevronRight className="h-3.5 w-3.5" />
            </button>
          )}
          {connected && (
            <Button
              size="sm"
              variant="ghost"
              className="h-7 gap-1.5 px-2"
              onClick={() => sync.mutate()}
              disabled={sync.isPending}
              aria-label="Sync now"
            >
              <RefreshCw className={cn("h-3.5 w-3.5", sync.isPending && "animate-spin")} />
            </Button>
          )}
          {needsConsent && (
            <>
              {status === "needs_reconsent" && <M365StatusBadge status={status} />}
              <Button
                size="sm"
                variant="outline"
                className="gap-1.5"
                onClick={() => connect.mutate()}
                disabled={connect.isPending}
              >
                <Plug className="h-3.5 w-3.5" />
                {status === "needs_reconsent" ? "Reconnect" : "Connect with M365"}
              </Button>
            </>
          )}
          {status === "error" && (
            <>
              <M365StatusBadge status={status} />
              <Button
                size="sm"
                variant="ghost"
                className="h-7 gap-1.5 px-2"
                onClick={() => sync.mutate()}
                disabled={sync.isPending}
                aria-label="Retry sync"
              >
                <RefreshCw className={cn("h-3.5 w-3.5", sync.isPending && "animate-spin")} />
              </Button>
            </>
          )}
        </div>
      </td>
    </tr>
  );
}

// ---- article selection dialog (the gear) ------------------------------

const PAGE_SIZE = 50;

/// Pick which Adsolut articles count as "Microsoft 365 related". Seeds from the
/// saved selection, lets the agent search the full catalogue and tick one or
/// more, then persists on Save and refreshes the companies list. Selected
/// articles are tracked as id → label so chips render even for articles not on
/// the current search page.
function ArticleSelectionDialog({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const qc = useQueryClient();
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  // id → {code,name}. The presence of a key means "selected".
  const [selected, setSelected] = useState<Map<string, M365SelectedArticle>>(new Map());
  const [seeded, setSeeded] = useState(false);

  const selection = useQuery({
    queryKey: SELECTION_KEY,
    queryFn: () => contractM365Api.getSelection(),
    enabled: open,
  });

  // Seed the local selection once per open, from the saved set.
  useEffect(() => {
    if (!open) {
      setSeeded(false);
      setSearch("");
      setPage(1);
      return;
    }
    if (seeded || selection.data === undefined) return;
    const next = new Map<string, M365SelectedArticle>();
    for (const a of selection.data.articles) next.set(a.id, a);
    setSelected(next);
    setSeeded(true);
  }, [open, seeded, selection.data]);

  const list = useQuery({
    queryKey: ["contracts", "m365", "article-picker", search, page],
    queryFn: () => articlesApi.list(search, page, PAGE_SIZE, "code", "asc", false),
    enabled: open,
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const pageItems = list.data?.items ?? [];

  const save = useMutation({
    mutationFn: () => contractM365Api.saveSelection(Array.from(selected.keys())),
    onSuccess: () => {
      toast.success("Microsoft 365 articles saved.");
      qc.invalidateQueries({ queryKey: COMPANIES_KEY });
      qc.invalidateQueries({ queryKey: SELECTION_KEY });
      onOpenChange(false);
    },
    onError: () => toast.error("Could not save the article selection."),
  });

  const toggle = (a: M365SelectedArticle) => {
    setSelected((prev) => {
      const next = new Map(prev);
      if (next.has(a.id)) next.delete(a.id);
      else next.set(a.id, a);
      return next;
    });
  };

  const selectedList = useMemo(() => Array.from(selected.values()), [selected]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>Microsoft 365 articles</DialogTitle>
          <DialogDescription>
            Pick the contract articles that relate to Microsoft 365. A company appears in the list when
            one or more of its contract lines reference any of these articles.
          </DialogDescription>
        </DialogHeader>

        {selectedList.length > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {selectedList.map((a) => (
              <button
                key={a.id}
                type="button"
                onClick={() => toggle(a)}
                className="inline-flex items-center gap-1.5 rounded-full border border-glass-strong bg-glass px-2.5 py-1 text-xs text-foreground transition-colors hover:bg-glass-hover"
              >
                <span className="font-mono text-[11px] text-muted-foreground">{a.code ?? "—"}</span>
                <span className="max-w-[14rem] truncate">{a.name || a.code || "—"}</span>
                <X className="h-3 w-3 text-muted-foreground" />
              </button>
            ))}
          </div>
        )}

        <div className="relative">
          <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setPage(1);
            }}
            placeholder="Search code, name…"
            className="pl-9"
          />
        </div>

        <div className="h-72 overflow-y-auto rounded-xl border border-glass bg-glass">
          {list.isLoading ? (
            <div className="space-y-2 p-3">
              <Skeleton className="h-8 w-full" />
              <Skeleton className="h-8 w-full" />
              <Skeleton className="h-8 w-full" />
            </div>
          ) : pageItems.length === 0 ? (
            <div className="flex h-full items-center justify-center p-6 text-center text-sm text-muted-foreground">
              {search.trim() ? "No articles match your search." : "No articles mirrored yet."}
            </div>
          ) : (
            <ul className="divide-y divide-glass">
              {pageItems.map((a) => {
                const isOn = selected.has(a.id);
                return (
                  <li key={a.id}>
                    <button
                      type="button"
                      onClick={() => toggle({ id: a.id, code: a.code, name: a.name })}
                      className="flex w-full items-center gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-glass-hover"
                    >
                      <span
                        className={cn(
                          "flex h-4 w-4 flex-none items-center justify-center rounded border",
                          isOn
                            ? "border-purple-400 bg-purple-400/20 text-purple-200"
                            : "border-glass-strong bg-glass",
                        )}
                      >
                        {isOn && <Check className="h-3 w-3" />}
                      </span>
                      <span className="w-28 flex-none font-mono text-xs text-muted-foreground">
                        {a.code ?? "—"}
                      </span>
                      <span className="truncate text-foreground">{a.name || a.code || "—"}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>

        <DialogFooter className="flex-row items-center justify-between gap-2 sm:justify-between">
          <div className="flex items-center gap-3 text-xs text-muted-foreground">
            <span>{selected.size} selected</span>
            {total > PAGE_SIZE && (
              <span className="flex items-center gap-1">
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-7 px-2"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={page <= 1 || list.isFetching}
                >
                  Prev
                </Button>
                Page {page}/{totalPages}
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-7 px-2"
                  onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                  disabled={page >= totalPages || list.isFetching}
                >
                  Next
                </Button>
              </span>
            )}
          </div>
          <div className="flex items-center gap-2">
            <Button size="sm" variant="ghost" onClick={() => onOpenChange(false)} disabled={save.isPending}>
              Cancel
            </Button>
            <Button size="sm" onClick={() => save.mutate()} disabled={save.isPending}>
              Save
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
