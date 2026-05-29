import { useEffect, useState, type CSSProperties } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowDown,
  ArrowUp,
  Filter,
  Globe,
  Plug,
  RefreshCw,
  Search,
  Server,
  Wifi,
  WifiOff,
} from "lucide-react";
import {
  assetsApi,
  trmmAdminApi,
  type AssetEolStatus,
  type AssetListItem,
  type AssetSort,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { AssetDetailSheet } from "./AssetDetailSheet";
import { cn } from "@/lib/utils";

const TYPE_OPTIONS = [
  { value: "all", label: "All" },
  { value: "server", label: "Servers" },
  { value: "workstation", label: "Workstations" },
];

const SORT_OPTIONS: { value: AssetSort; label: string }[] = [
  { value: "build_desc", label: "Build · newest first" },
  { value: "build_asc", label: "Build · oldest first" },
  { value: "hostname_asc", label: "Hostname · A→Z" },
  { value: "hostname_desc", label: "Hostname · Z→A" },
  { value: "last_seen_desc", label: "Last seen · recent first" },
  { value: "last_seen_asc", label: "Last seen · oldest first" },
  { value: "client_asc", label: "Client · A→Z" },
];

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

/// Tone for the OS-build chip. Newest builds (24H2, 23H2) get the
/// "fresh" emerald tone; the previous wave (22H2) goes amber; anything
/// older is rose to nudge an upgrade.
function buildTone(build: string | null): string {
  if (!build) return "border-glass-strong bg-glass-strong text-muted-foreground";
  const upper = build.toUpperCase();
  if (upper.includes("24H2") || upper.includes("25H2"))
    return "border-emerald-400/30 bg-emerald-500/10 text-emerald-300";
  if (upper.includes("23H2"))
    return "border-sky-400/30 bg-sky-500/10 text-sky-300";
  if (upper.includes("22H2"))
    return "border-amber-400/30 bg-amber-500/10 text-amber-300";
  if (upper.includes("SERVER 2022") || upper.includes("SERVER 2019"))
    return "border-sky-400/30 bg-sky-500/10 text-sky-300";
  return "border-rose-400/30 bg-rose-500/10 text-rose-300";
}

export function AssetsPage() {
  const qc = useQueryClient();
  const navigate = useNavigate();

  const [search, setSearch] = useState("");
  const [type, setType] = useState<string>("all");
  const [buildFilter, setBuildFilter] = useState<string[]>([]);
  const [onlineOnly, setOnlineOnly] = useState(false);
  const [eolStatusFilter, setEolStatusFilter] = useState<string>("all");
  const [sort, setSort] = useState<AssetSort>("build_desc");
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const [openId, setOpenId] = useState<string | null>(null);

  useEffect(() => {
    setPage(1);
  }, [search, type, buildFilter, onlineOnly, eolStatusFilter, sort]);

  const syncState = useQuery({
    queryKey: ["assets", "sync-state"] as const,
    queryFn: assetsApi.syncState,
    staleTime: 10_000,
  });

  const builds = useQuery({
    queryKey: ["assets", "builds"] as const,
    queryFn: assetsApi.listBuilds,
    staleTime: 60_000,
  });

  const list = useQuery({
    queryKey: [
      "assets",
      "list",
      { search, type, buildFilter, onlineOnly, eolStatusFilter, sort, page },
    ] as const,
    queryFn: () =>
      assetsApi.list({
        search,
        type: type === "all" ? "" : (type as "server" | "workstation"),
        builds: buildFilter.length > 0 ? buildFilter : undefined,
        online: onlineOnly ? true : undefined,
        eolStatus:
          eolStatusFilter === "all" ? "" : (eolStatusFilter as AssetEolStatus),
        sort,
        page,
        pageSize,
      }),
    staleTime: 5_000,
  });

  const triggerSync = useMutation({
    mutationFn: () => trmmAdminApi.triggerSync(),
    onSuccess: (result) => {
      if (result.success) {
        toast.success(`Synced ${result.agents} agents (${result.latencyMs} ms)`);
        qc.invalidateQueries({ queryKey: ["assets"] });
      } else {
        toast.error(result.errorMessage ?? "Sync failed");
      }
    },
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  const trmmDisabled = syncState.data && !syncState.data.enabled;
  const neverSynced = syncState.data?.lastSyncUtc == null;

  return (
    <div className="flex flex-col gap-6 p-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Server className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Assets</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Servers and workstations mirrored from Tactical RMM. Filter on Windows build
            to see which clients still need an OS update.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span className="text-xs text-muted-foreground">
            Last sync: <strong className="text-foreground">{relativeTime(syncState.data?.lastSyncUtc ?? null)}</strong>
            {syncState.data?.lastStatus === "failed" && (
              <span className="ml-1 text-amber-400">— {syncState.data.lastError ?? "failed"}</span>
            )}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => triggerSync.mutate()}
            disabled={!syncState.data?.enabled || triggerSync.isPending}
          >
            {triggerSync.isPending ? (
              <>
                <RefreshCw className="mr-1.5 h-3 w-3 animate-spin" /> Syncing…
              </>
            ) : (
              <>
                <RefreshCw className="mr-1.5 h-3 w-3" /> Sync now
              </>
            )}
          </Button>
        </div>
      </header>

      {(trmmDisabled || neverSynced) && (
        <div className="rounded-lg border border-amber-400/30 bg-amber-500/[0.08] p-4 text-sm text-amber-200">
          <div className="flex items-start gap-3">
            <Plug className="mt-0.5 h-4 w-4 shrink-0" />
            <div className="flex-1">
              <p className="font-medium">
                {trmmDisabled
                  ? "Tactical RMM integration is disabled."
                  : "No sync has run yet."}
              </p>
              <p className="mt-1 text-xs text-amber-200/80">
                {trmmDisabled
                  ? "Configure the connection and enable it under Settings → Integrations → Tactical RMM."
                  : "Configure the base URL + API key and run a first sync to populate this list."}
              </p>
              <Link
                to="/settings/integrations/trmm"
                className="mt-2 inline-flex items-center gap-1 text-xs underline"
              >
                Open TRMM settings →
              </Link>
            </div>
          </div>
        </div>
      )}

      <section className="rounded-lg border border-glass bg-glass p-4">
        <div className="flex flex-wrap items-center gap-2">
          <div className="relative min-w-[220px] flex-1">
            <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Hostname, client, site…"
              className="pl-7"
            />
          </div>
          <Select value={type} onValueChange={setType}>
            <SelectTrigger className="w-36">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {TYPE_OPTIONS.map((o) => (
                <SelectItem key={o.value} value={o.value}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Select
            value={buildFilter.length === 1 ? buildFilter[0] : "all"}
            onValueChange={(v) => setBuildFilter(v === "all" ? [] : [v])}
          >
            <SelectTrigger className="w-44">
              <Filter className="mr-1 h-3 w-3" />
              <SelectValue placeholder="Build" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All builds</SelectItem>
              {(builds.data?.items ?? []).map((b) => (
                <SelectItem key={b} value={b}>
                  {b}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            size="sm"
            variant={onlineOnly ? "default" : "outline"}
            onClick={() => setOnlineOnly((v) => !v)}
          >
            {onlineOnly ? <Wifi className="mr-1 h-3 w-3" /> : <WifiOff className="mr-1 h-3 w-3" />}
            Online only
          </Button>
          <Select value={eolStatusFilter} onValueChange={setEolStatusFilter}>
            <SelectTrigger className="w-40">
              <SelectValue placeholder="EOL status" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All EOL states</SelectItem>
              <SelectItem value="expired">EOL — expired</SelectItem>
              <SelectItem value="soon">EOL — soon</SelectItem>
              <SelectItem value="active">Active</SelectItem>
              <SelectItem value="unknown">Unknown</SelectItem>
            </SelectContent>
          </Select>
          <div className="ml-auto flex items-center gap-2">
            <Select value={sort} onValueChange={(v) => setSort(v as AssetSort)}>
              <SelectTrigger className="w-52">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {SORT_OPTIONS.map((o) => (
                  <SelectItem key={o.value} value={o.value}>
                    {o.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </div>
      </section>

      <section className="overflow-hidden rounded-lg border border-glass bg-glass">
        <table className="w-full text-sm">
          <thead className="bg-glass-strong/60 text-xs text-muted-foreground">
            <tr>
              <th className="px-3 py-2 text-left">Hostname</th>
              <th className="px-3 py-2 text-left">Type</th>
              <th className="px-3 py-2 text-left">OS family</th>
              <th className="px-3 py-2 text-left">Build</th>
              <th className="px-3 py-2 text-left">EOL</th>
              <th className="px-3 py-2 text-left">Last seen</th>
              <th className="px-3 py-2 text-left">Client</th>
              <th className="px-3 py-2 text-left">Site</th>
              <th className="px-3 py-2 text-left">Public IP</th>
            </tr>
          </thead>
          <tbody>
            {list.isLoading ? (
              <tr>
                <td colSpan={9} className="p-3">
                  <Skeleton className="h-20 w-full" />
                </td>
              </tr>
            ) : list.data?.items.length === 0 ? (
              <tr>
                <td colSpan={9} className="px-3 py-8 text-center text-xs text-muted-foreground">
                  No assets match the current filters.
                </td>
              </tr>
            ) : (
              list.data?.items.map((row) => (
                <AssetRow key={row.id} row={row} onClick={() => setOpenId(row.id)} />
              ))
            )}
          </tbody>
        </table>
      </section>

      <footer className="flex items-center justify-between text-xs text-muted-foreground">
        <span>
          {total === 0
            ? "0 assets"
            : `Showing ${(page - 1) * pageSize + 1}–${Math.min(page * pageSize, total)} of ${total}`}
        </span>
        <div className="flex items-center gap-2">
          <Button
            size="sm"
            variant="ghost"
            disabled={page <= 1}
            onClick={() => setPage((p) => Math.max(1, p - 1))}
          >
            Prev
          </Button>
          <span>
            Page {page} of {totalPages}
          </span>
          <Button
            size="sm"
            variant="ghost"
            disabled={page >= totalPages}
            onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
          >
            Next
          </Button>
        </div>
      </footer>

      <AssetDetailSheet
        assetId={openId}
        onClose={() => setOpenId(null)}
      />
    </div>
  );
}

/// Per-row tint that mirrors the ticket-list priority pattern: a 3px
/// inset shadow on the left edge, plus a faint gradient fading away to
/// the right. Red for past-EOL, amber for soon-to-be-EOL, nothing for
/// active or unknown so the table stays calm by default.
function eolRowStyle(status: AssetEolStatus): CSSProperties | undefined {
  if (status === "expired") {
    const c = "#f43f5e"; // rose-500
    return {
      boxShadow: `inset 3px 0 0 0 ${c}`,
      backgroundImage: `linear-gradient(to right, ${c}1f 0%, ${c}0d 30%, transparent 60%)`,
    };
  }
  if (status === "soon") {
    const c = "#f59e0b"; // amber-500
    return {
      boxShadow: `inset 3px 0 0 0 ${c}`,
      backgroundImage: `linear-gradient(to right, ${c}1f 0%, ${c}0d 30%, transparent 60%)`,
    };
  }
  return undefined;
}

function eolChipTone(status: AssetEolStatus): string {
  switch (status) {
    case "expired":
      return "border-rose-400/40 bg-rose-500/15 text-rose-300";
    case "soon":
      return "border-amber-400/40 bg-amber-500/15 text-amber-300";
    case "active":
      return "border-emerald-400/30 bg-emerald-500/10 text-emerald-300";
    default:
      return "border-glass bg-glass-strong text-muted-foreground";
  }
}

function eolChipLabel(status: AssetEolStatus): string {
  switch (status) {
    case "expired": return "EOL";
    case "soon":    return "Soon";
    case "active":  return "Active";
    default:        return "Unknown";
  }
}

function formatEolTooltip(eolUtc: string | null, status: AssetEolStatus): string {
  if (!eolUtc) return "No end-of-life data available";
  const d = new Date(eolUtc);
  const label = d.toLocaleDateString();
  if (status === "expired") return `End of support: ${label} (past)`;
  if (status === "soon")    return `End of support: ${label}`;
  return `End of support: ${label}`;
}

function AssetRow({ row, onClick }: { row: AssetListItem; onClick: () => void }) {
  return (
    <tr
      className="cursor-pointer border-t border-glass transition-colors hover:bg-glass-strong/40"
      style={eolRowStyle(row.eolStatus)}
      onClick={onClick}
    >
      <td className="px-3 py-2 font-medium text-foreground">
        <div className="flex items-center gap-2">
          <span
            className={cn(
              "inline-block h-1.5 w-1.5 rounded-full",
              row.online ? "bg-emerald-400" : "bg-glass-strong",
            )}
            title={row.online ? "Online" : "Offline"}
          />
          {row.hostname}
        </div>
      </td>
      <td className="px-3 py-2">
        <Badge className="border border-glass bg-glass-strong text-[10px] font-normal capitalize">
          {row.agentType}
        </Badge>
      </td>
      <td className="px-3 py-2 text-foreground">
        {row.osFamily ? (
          <span className="text-xs">{row.osFamily}</span>
        ) : (
          <span className="text-xs text-muted-foreground">{row.osName ?? "—"}</span>
        )}
      </td>
      <td className="px-3 py-2 text-muted-foreground">
        {row.osBuild ? (
          <Badge className={cn("border text-[10px] font-normal", buildTone(row.osBuild))}>
            {row.osBuild}
          </Badge>
        ) : (
          <span className="text-xs">—</span>
        )}
      </td>
      <td className="px-3 py-2">
        <Badge
          className={cn("border text-[10px] font-normal", eolChipTone(row.eolStatus))}
          title={formatEolTooltip(row.eolUtc, row.eolStatus)}
        >
          {eolChipLabel(row.eolStatus)}
        </Badge>
      </td>
      <td className="px-3 py-2 text-muted-foreground">{relativeTime(row.lastSeenUtc)}</td>
      <td className="px-3 py-2">
        <div className="flex flex-col">
          <span className="text-foreground">{row.clientName}</span>
          {row.companyName && row.companyName !== row.clientName && (
            <span className="text-[10px] text-muted-foreground">
              → {row.companyName}
            </span>
          )}
        </div>
      </td>
      <td className="px-3 py-2 text-muted-foreground">{row.siteName}</td>
      <td className="px-3 py-2 text-muted-foreground">{row.publicIp ?? "—"}</td>
    </tr>
  );
}
