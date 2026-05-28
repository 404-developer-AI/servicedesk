import { useMemo, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlertCircle,
  ArrowLeft,
  CheckCircle2,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Database,
  Filter,
  Globe,
  History,
  KeyRound,
  RefreshCw,
  Search,
  Ticket,
  Trash2,
  X,
} from "lucide-react";
import { useNavigate } from "@tanstack/react-router";
import {
  ApiError,
  apiErrorMessage,
  settingsApi,
  zammadAdminApi,
  zammadDryRunApi,
  zammadMappingApi,
  type SettingEntry,
  type ZammadConnectionState,
  type ZammadDryRunStartRequest,
  type ZammadTestResult,
  type ZammadTicketSearchPage,
} from "@/lib/api";
import { ZammadMappingSection, MAPPING_QK } from "./zammad/ZammadMappingSection";
import { ZammadKbImportSection } from "./zammad/ZammadKbImportSection";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { IntegrationAuditLog } from "@/components/integrations/IntegrationAuditLog";
import { cn } from "@/lib/utils";

const STATUS_QK = ["integrations", "zammad", "status"] as const;
const SECRET_QK = ["integrations", "zammad", "secret"] as const;
const SETTINGS_QK = ["settings", "list", "Zammad"] as const;
const GROUPS_QK = ["integrations", "zammad", "groups"] as const;
const STATES_QK = ["integrations", "zammad", "states"] as const;

const STATE_LABEL: Record<
  ZammadConnectionState,
  { tone: string; text: string; dot: string }
> = {
  Disabled: {
    tone: "border-glass-strong bg-glass-strong text-muted-foreground",
    text: "Disabled",
    dot: "bg-glass-strong",
  },
  NotConfigured: {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    text: "Not configured",
    dot: "bg-amber-400",
  },
  Ready: {
    tone: "border-sky-400/30 bg-sky-500/10 text-sky-300",
    text: "Ready — run Test connection",
    dot: "bg-sky-400",
  },
};

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
}

export function ZammadIntegrationPage() {
  const qc = useQueryClient();

  const status = useQuery({ queryKey: STATUS_QK, queryFn: zammadAdminApi.status });
  const secret = useQuery({ queryKey: SECRET_QK, queryFn: zammadAdminApi.secretStatus });
  const settingsList = useQuery({
    queryKey: SETTINGS_QK,
    queryFn: () => settingsApi.list("Zammad"),
  });

  const hasToken = secret.data?.configured ?? false;
  const baseUrl = status.data?.baseUrl ?? "";

  const [tokenDraft, setTokenDraft] = useState("");
  const [urlDraft, setUrlDraft] = useState("");
  const [lastTest, setLastTest] = useState<{
    at: number;
    result: ZammadTestResult;
  } | null>(null);

  const saveSecret = useMutation({
    mutationFn: () => zammadAdminApi.setSecret(tokenDraft),
    onSuccess: () => {
      toast.success("API token saved");
      setTokenDraft("");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      toast.error(
        upstream
          ? `Save failed: ${upstream}`
          : err instanceof ApiError
            ? `Save failed (${err.status})`
            : "Save failed",
      );
    },
  });

  const deleteSecret = useMutation({
    mutationFn: () => zammadAdminApi.deleteSecret(),
    onSuccess: () => {
      toast.success("API token cleared");
      setLastTest(null);
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
  });

  const saveBaseUrl = useMutation({
    mutationFn: () => zammadAdminApi.setBaseUrl(urlDraft.trim()),
    onSuccess: () => {
      toast.success("Base URL saved");
      setUrlDraft("");
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      toast.error(
        upstream
          ? `Save failed: ${upstream}`
          : err instanceof ApiError
            ? `Save failed (${err.status})`
            : "Save failed",
      );
    },
  });

  const test = useMutation({
    mutationFn: () => zammadAdminApi.testConnection(),
    onSuccess: (result) => {
      setLastTest({ at: Date.now(), result });
      const who =
        result.me.email ||
        [result.me.firstName, result.me.lastName].filter(Boolean).join(" ") ||
        result.me.login ||
        `user #${result.me.id}`;
      const versionPart = result.version ? ` (Zammad ${result.version})` : "";
      toast.success(`Connected as ${who}${versionPart}`);
    },
    onError: (err) => {
      const upstream = apiErrorMessage(err);
      const status = err instanceof ApiError ? err.status : null;
      toast.error(
        upstream
          ? `Test failed: ${upstream}`
          : status
            ? `Test failed (HTTP ${status})`
            : "Test failed — check the base URL and token",
      );
    },
  });

  const canTest =
    (status.data?.enabled ?? false) &&
    hasToken &&
    (baseUrl?.length ?? 0) > 0 &&
    !test.isPending;

  const state = status.data?.state ?? "Disabled";
  const stateMeta = STATE_LABEL[state];

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <Link
            to="/settings/integrations"
            className="inline-flex items-center gap-1 text-xs text-muted-foreground/70 transition-colors hover:text-foreground"
          >
            <ArrowLeft className="h-3 w-3" /> Integrations
          </Link>
          <div className="mt-2 mb-2 text-primary">
            <Database className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Zammad</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            One-way migration link from an existing Zammad install into this
            Servicedesk. Phase 1 ships connectivity + Test connection only —
            ticket picker, dry-run and import land in subsequent phases of
            v0.0.41.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span
            className={cn(
              "inline-flex items-center gap-2 rounded-full border px-3 py-1 text-xs font-medium",
              stateMeta.tone,
            )}
          >
            <span className={cn("h-1.5 w-1.5 rounded-full", stateMeta.dot)} />
            {stateMeta.text}
          </span>
          <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
            Admin only
          </Badge>
        </div>
      </header>

      {/* ---- Base URL ------------------------------------------------ */}
      <section className="space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Globe className="h-4 w-4 text-muted-foreground" />
          Base URL
        </div>
        <p className="text-xs text-muted-foreground/70">
          Root URL of the source Zammad instance, without a trailing slash or
          path. The client appends <code className="font-mono">/api/v1/...</code> itself.
          HTTPS required for any non-localhost host.
        </p>
        <div className="flex flex-wrap gap-2">
          <Input
            type="url"
            inputMode="url"
            autoComplete="off"
            placeholder={baseUrl || "https://desk.example.com"}
            value={urlDraft}
            onChange={(e) => setUrlDraft(e.target.value)}
            className="h-9 w-96 bg-glass font-mono text-sm"
          />
          <Button
            size="sm"
            className="h-9"
            disabled={
              urlDraft.trim().length === 0 ||
              urlDraft.trim() === baseUrl ||
              saveBaseUrl.isPending
            }
            onClick={() => saveBaseUrl.mutate()}
          >
            {baseUrl ? "Replace" : "Save"}
          </Button>
          {baseUrl ? (
            <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
              <CheckCircle2 className="h-3 w-3" />
              <span className="font-mono">{baseUrl}</span>
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 text-xs text-muted-foreground/60">
              <AlertCircle className="h-3 w-3" /> No URL configured
            </span>
          )}
        </div>
      </section>

      {/* ---- API token ----------------------------------------------- */}
      <section className="space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <KeyRound className="h-4 w-4 text-muted-foreground" />
          API token
        </div>
        <p className="text-xs text-muted-foreground/70">
          Personal access token from the source Zammad instance (My Profile →
          Token Access). Needs ticket + user + organization permissions.
          Stored encrypted via the DataProtection key-ring; never echoed back.
        </p>
        <div className="flex flex-wrap gap-2">
          <Input
            type="password"
            autoComplete="new-password"
            placeholder={hasToken ? "Replace token…" : "Paste API token…"}
            value={tokenDraft}
            onChange={(e) => setTokenDraft(e.target.value)}
            className="h-9 w-80 bg-glass font-mono text-sm"
          />
          <Button
            size="sm"
            className="h-9"
            disabled={tokenDraft.trim().length === 0 || saveSecret.isPending}
            onClick={() => saveSecret.mutate()}
          >
            {hasToken ? "Replace" : "Save"}
          </Button>
          {hasToken && (
            <Button
              size="sm"
              variant="ghost"
              className="h-9 text-muted-foreground hover:text-foreground"
              disabled={deleteSecret.isPending}
              onClick={() => deleteSecret.mutate()}
            >
              <Trash2 className="mr-1.5 h-3.5 w-3.5" />
              Clear
            </Button>
          )}
          <Button
            size="sm"
            variant="ghost"
            className="h-9"
            disabled={!canTest}
            onClick={() => test.mutate()}
            title={
              !status.data?.enabled
                ? "Enable the integration first (Behaviour section below)"
                : !hasToken
                  ? "Save an API token first"
                  : !baseUrl
                    ? "Save a base URL first"
                    : undefined
            }
          >
            <RefreshCw className={cn("mr-1.5 h-3.5 w-3.5", test.isPending && "animate-spin")} />
            Test connection
          </Button>
          {hasToken ? (
            <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
              <CheckCircle2 className="h-3 w-3" /> Token saved
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 text-xs text-muted-foreground/60">
              <AlertCircle className="h-3 w-3" /> No token configured
            </span>
          )}
        </div>

        {lastTest && (
          <div className="rounded-md border border-emerald-400/20 bg-emerald-500/[0.05] p-3 text-xs">
            <div className="flex items-center gap-2 text-emerald-300">
              <CheckCircle2 className="h-3.5 w-3.5" />
              Last successful test {new Date(lastTest.at).toLocaleString()}
            </div>
            <div className="mt-2 grid grid-cols-2 gap-x-6 gap-y-1 text-muted-foreground">
              <div>
                Connected as{" "}
                <span className="font-mono text-foreground">
                  {lastTest.result.me.email ||
                    lastTest.result.me.login ||
                    `user #${lastTest.result.me.id}`}
                </span>
              </div>
              <div>
                Zammad version{" "}
                <span className="font-mono text-foreground">
                  {lastTest.result.version ?? "unknown"}
                </span>
              </div>
              <div>
                Round-trip{" "}
                <span className="tabular-nums text-foreground">
                  {lastTest.result.latencyMs} ms
                </span>
              </div>
              {lastTest.result.me.firstName || lastTest.result.me.lastName ? (
                <div>
                  Display name{" "}
                  <span className="text-foreground">
                    {[lastTest.result.me.firstName, lastTest.result.me.lastName]
                      .filter(Boolean)
                      .join(" ")}
                  </span>
                </div>
              ) : null}
            </div>
          </div>
        )}
      </section>

      {/* ---- Mapping (groups / states / priorities) ------------------ */}
      <ZammadMappingSection ready={state === "Ready"} />

      {/* ---- Ticket picker ------------------------------------------ */}
      <TicketPickerSection ready={state === "Ready"} />

      {/* ---- Behaviour ----------------------------------------------- */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-3 text-sm font-medium text-foreground">Behaviour</div>
        {settingsList.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : (
          <>
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Zammad.Enabled")}
              queryKey={SETTINGS_QK}
              label="Enabled"
              hint="Master kill-switch. When off, every Zammad endpoint refuses with 409 so an in-progress dry-run can't accidentally fire. Token and base URL stay editable so an admin can configure the connection without flipping it on."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Zammad.PerPageDefault")}
              queryKey={SETTINGS_QK}
              label="Page size (default)"
              hint="Default per-page used by the ticket picker proxy. Range 1–200. Higher = fewer round-trips for bulk listings, but more memory per request."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Zammad.MaxRequestsPerSecond")}
              queryKey={SETTINGS_QK}
              label="Max requests per second"
              hint="Defensive client-side rate cap on outbound Zammad calls. Zammad publishes no rate limit; this prevents a bulk dry-run from hammering a production helpdesk. Range 1–20."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Zammad.RetryBaseSeconds")}
              queryKey={SETTINGS_QK}
              label="Retry base (seconds)"
              hint="Base delay for exponential backoff on 429 / 5xx responses. Actual delay = base × 2^attempt ± 20% jitter. Range 1–60."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Zammad.RetryMaxAttempts")}
              queryKey={SETTINGS_QK}
              label="Retry max attempts"
              hint="How many times a transient failure is retried before surfacing as a hard error. Range 0–8."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Zammad.DryRunRetentionDays")}
              queryKey={SETTINGS_QK}
              label="Dry-run retention (days)"
              hint="Dry-run snapshots are heavy (full per-ticket mapping verdict). The sweeper prunes anything older than this. Range 1–90."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Zammad.SelectAllMatchingHardCap")}
              queryKey={SETTINGS_QK}
              label="Select-all-matching hard cap"
              hint="Maximum tickets walked per &quot;Select all matching&quot; dry-run or import. Stops a runaway free-text filter from chewing the whole upstream. Range 100–200000."
            />
          </>
        )}
      </section>

      {/* ---- Integration audit log ---------------------------------- */}
      <section className="space-y-3 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <History className="h-4 w-4 text-muted-foreground" />
          Audit log
        </div>
        <p className="text-xs text-muted-foreground/70">
          Every outbound Zammad API call writes a row here with latency,
          HTTP status and upstream error code. The first place to look when
          a Test connection returns 502.
        </p>
        <IntegrationAuditLog integration="zammad" />
      </section>

      {/* ---- KB import (v0.0.43) ----------------------------------- */}
      <ZammadKbImportSection />
    </div>
  );
}

// =====================================================================
// Ticket picker (fase 2). Free-text + multi-select groups + multi-select
// states compose into a single Zammad ES-query that the backend proxies.
// Per-row checkboxes for surgical "import these 3" selection or a
// "Select all matching" toggle that captures the filter itself (used by
// fase 4 bulk-import). No import button yet — that lands in fase 3+.
// =====================================================================

function TicketPickerSection({ ready }: { ready: boolean }) {
  const [textDraft, setTextDraft] = useState("");
  const [textActive, setTextActive] = useState("");
  const [selectedGroupIds, setSelectedGroupIds] = useState<number[]>([]);
  const [selectedStateIds, setSelectedStateIds] = useState<number[]>([]);
  const [page, setPage] = useState(1);
  const [selectedTicketIds, setSelectedTicketIds] = useState<Set<number>>(new Set());
  const [selectAllMatching, setSelectAllMatching] = useState(false);

  const groupsQuery = useQuery({
    queryKey: GROUPS_QK,
    queryFn: () => zammadAdminApi.listGroups(),
    enabled: ready,
    staleTime: 5 * 60 * 1000,
  });
  const statesQuery = useQuery({
    queryKey: STATES_QK,
    queryFn: () => zammadAdminApi.listStates(),
    enabled: ready,
    staleTime: 5 * 60 * 1000,
  });

  // Search-query key changes whenever the *committed* filter values
  // change (not the live draft). Keeps the result table stable while the
  // admin is still typing in the free-text box.
  const SEARCH_QK = [
    "integrations",
    "zammad",
    "tickets-search",
    textActive,
    [...selectedGroupIds].sort().join(","),
    [...selectedStateIds].sort().join(","),
    page,
  ] as const;
  const searchQuery = useQuery<ZammadTicketSearchPage, Error>({
    queryKey: SEARCH_QK,
    queryFn: () =>
      zammadAdminApi.searchTickets({
        q: textActive,
        groupIds: selectedGroupIds,
        stateIds: selectedStateIds,
        page,
      }),
    enabled: ready,
    // The picker stays mounted on the integration page — keep stale data
    // visible while a new page loads so the row count doesn't flicker
    // when paging.
    placeholderData: (prev) => prev,
    staleTime: 30_000,
  });

  function runSearch() {
    // Commit the draft + reset the page cursor + drop any prior
    // surgical selection (those ids may not appear in the new result).
    setTextActive(textDraft.trim());
    setPage(1);
    setSelectedTicketIds(new Set());
  }

  function changeFilter(next: () => void) {
    next();
    setPage(1);
    setSelectedTicketIds(new Set());
  }

  function toggleRowSelection(id: number) {
    setSelectedTicketIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  const groups = groupsQuery.data?.items ?? [];
  const states = statesQuery.data?.items ?? [];
  const result = searchQuery.data;
  const totalPages =
    result && result.total !== null
      ? Math.max(1, Math.ceil(result.total / result.perPage))
      : null;
  const selectionCount = selectAllMatching
    ? result?.total ?? null
    : selectedTicketIds.size;

  return (
    <section className="space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Ticket className="h-4 w-4 text-muted-foreground" />
          Ticket picker
        </div>
        <SelectionBadge count={selectionCount} matchingMode={selectAllMatching} />
      </div>
      <p className="text-xs text-muted-foreground/70">
        Search the source Zammad instance with free text and structured
        filters. Selection is captured here for fase 3 (dry-run) and fase
        4 (import) — no upstream ticket is touched yet.
      </p>

      {!ready ? (
        <div className="rounded-md border border-amber-400/20 bg-amber-500/[0.05] p-3 text-xs text-amber-200">
          Save a base URL + token and toggle <span className="font-mono">Zammad.Enabled</span> on
          first. The picker reads Zammad live.
        </div>
      ) : (
        <>
          {/* ---- filter row -------------------------------------- */}
          <div className="flex flex-wrap items-center gap-2">
            <div className="relative">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground/60" />
              <Input
                value={textDraft}
                onChange={(e) => setTextDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") runSearch();
                }}
                placeholder="Free text (ticket number, subject, customer)…"
                className="h-9 w-72 bg-glass pl-8 text-sm"
              />
            </div>
            <MultiSelectFilter
              label="Groups"
              icon={<Filter className="h-3 w-3" />}
              options={groups.filter((g) => g.active).map((g) => ({ id: g.id, label: g.name }))}
              selected={selectedGroupIds}
              loading={groupsQuery.isLoading}
              onChange={(ids) => changeFilter(() => setSelectedGroupIds(ids))}
            />
            <MultiSelectFilter
              label="States"
              icon={<Filter className="h-3 w-3" />}
              options={states.filter((s) => s.active).map((s) => ({ id: s.id, label: s.name }))}
              selected={selectedStateIds}
              loading={statesQuery.isLoading}
              onChange={(ids) => changeFilter(() => setSelectedStateIds(ids))}
            />
            <Button
              size="sm"
              className="h-9"
              onClick={runSearch}
              disabled={searchQuery.isFetching}
            >
              <RefreshCw
                className={cn("mr-1.5 h-3.5 w-3.5", searchQuery.isFetching && "animate-spin")}
              />
              Search
            </Button>
            {(textActive ||
              selectedGroupIds.length > 0 ||
              selectedStateIds.length > 0) && (
              <Button
                size="sm"
                variant="ghost"
                className="h-9 text-muted-foreground hover:text-foreground"
                onClick={() => {
                  setTextDraft("");
                  setTextActive("");
                  setSelectedGroupIds([]);
                  setSelectedStateIds([]);
                  setPage(1);
                  setSelectedTicketIds(new Set());
                  setSelectAllMatching(false);
                }}
              >
                <X className="mr-1 h-3 w-3" /> Reset
              </Button>
            )}
          </div>

          {/* ---- summary row + select-all-matching --------------- */}
          <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
            <div>
              {searchQuery.isLoading || searchQuery.isFetching ? (
                <span className="inline-flex items-center gap-1.5">
                  <RefreshCw className="h-3 w-3 animate-spin" /> Loading…
                </span>
              ) : result ? (
                result.total !== null ? (
                  <>
                    <span className="text-foreground">{result.total.toLocaleString()}</span>{" "}
                    {result.total === 1 ? "ticket" : "tickets"} match
                    {totalPages !== null && totalPages > 1 ? (
                      <>
                        {" "}
                        — page <span className="text-foreground">{result.page}</span> of{" "}
                        <span className="text-foreground">{totalPages}</span>
                      </>
                    ) : null}
                  </>
                ) : (
                  <>
                    Showing{" "}
                    <span className="text-foreground">{result.items.length}</span> result(s) —
                    total unknown (Zammad refused the count)
                  </>
                )
              ) : null}
            </div>
            <label className="inline-flex cursor-pointer items-center gap-2">
              <input
                type="checkbox"
                checked={selectAllMatching}
                onChange={(e) => {
                  setSelectAllMatching(e.target.checked);
                  if (e.target.checked) setSelectedTicketIds(new Set());
                }}
                disabled={!result || !result.total || result.total === 0}
                className="h-3.5 w-3.5 rounded border-glass-strong bg-glass accent-violet-500"
              />
              <span>Select all matching ({result?.total?.toLocaleString() ?? "—"})</span>
            </label>
          </div>

          {/* ---- result table ------------------------------------ */}
          <ResultTable
            page={result}
            loading={searchQuery.isLoading}
            error={searchQuery.isError ? searchQuery.error : null}
            selectedIds={selectedTicketIds}
            selectAllMatching={selectAllMatching}
            onToggleRow={toggleRowSelection}
            groups={groups}
            states={states}
          />

          {/* ---- pagination --------------------------------------- */}
          {result && result.items.length > 0 && (
            <div className="flex items-center justify-between gap-2 text-xs text-muted-foreground">
              <div>
                Per page <span className="text-foreground">{result.perPage}</span> · server-sorted
                by latest update
              </div>
              <div className="flex items-center gap-1">
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-7"
                  disabled={page <= 1 || searchQuery.isFetching}
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                >
                  <ChevronLeft className="h-3 w-3" />
                </Button>
                <span className="px-2">
                  Page <span className="text-foreground">{result.page}</span>
                  {totalPages !== null && <> / {totalPages}</>}
                </span>
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-7"
                  disabled={
                    searchQuery.isFetching ||
                    (totalPages !== null && page >= totalPages) ||
                    result.items.length < result.perPage
                  }
                  onClick={() => setPage((p) => p + 1)}
                >
                  <ChevronRight className="h-3 w-3" />
                </Button>
              </div>
            </div>
          )}

          {/* ---- dry-run / import actions ------------------------- */}
          <DryRunActions
            ticketIds={Array.from(selectedTicketIds)}
            freeText={textActive}
            groupIds={selectedGroupIds}
            stateIds={selectedStateIds}
            selectAllMatching={selectAllMatching}
            selectionCount={selectionCount}
          />
        </>
      )}
    </section>
  );
}

/// Bottom-action strip on the picker. Becomes live in fase 3 — the
/// Dry-run button starts a background run and navigates to the run-
/// detail page. The Import button stays disabled until fase 4 lands.
/// Mapping completeness is queried separately so the button can refuse
/// the click with a helpful message before hitting the backend.
function DryRunActions({
  ticketIds,
  freeText,
  groupIds,
  stateIds,
  selectAllMatching,
  selectionCount,
}: {
  ticketIds: number[];
  freeText: string;
  groupIds: number[];
  stateIds: number[];
  selectAllMatching: boolean;
  selectionCount: number | null;
}) {
  const navigate = useNavigate();
  const mappings = useQuery({
    queryKey: MAPPING_QK,
    queryFn: zammadMappingApi.overview,
    staleTime: 30_000,
  });
  const startMutation = useMutation({
    mutationFn: (req: ZammadDryRunStartRequest) => zammadDryRunApi.start(req),
    onSuccess: (res) => {
      toast.success("Dry-run started.");
      navigate({
        to: "/settings/integrations/zammad/runs/$runId",
        params: { runId: res.runId },
      });
    },
    onError: (err) => toast.error(apiErrorMessage(err)),
  });

  const hasExplicit = ticketIds.length > 0;
  const hasSelection = hasExplicit || selectAllMatching;
  const unmapped =
    (mappings.data?.unmappedGroupCount ?? 0) +
    (mappings.data?.unmappedStateCount ?? 0) +
    (mappings.data?.unmappedPriorityCount ?? 0);

  const disabledReason = !hasSelection
    ? "Select tickets or enable Select all matching first."
    : unmapped > 0
      ? `Map ${unmapped} remaining Zammad ${unmapped === 1 ? "item" : "items"} above before running.`
      : null;

  function handleClick() {
    if (disabledReason) return;
    startMutation.mutate({
      ticketIds: hasExplicit ? ticketIds : undefined,
      freeText: freeText || undefined,
      groupIds: groupIds.length > 0 ? groupIds : undefined,
      stateIds: stateIds.length > 0 ? stateIds : undefined,
      selectAllMatching: !hasExplicit && selectAllMatching,
    });
  }

  return (
    <div className="flex flex-wrap items-center justify-between gap-2 border-t border-glass pt-3">
      <Link
        to="/settings/integrations/zammad/runs"
        className="text-xs text-muted-foreground/70 underline-offset-2 hover:text-foreground hover:underline"
      >
        View previous runs →
      </Link>
      <div className="flex items-center gap-2">
        <Button
          size="sm"
          className="h-9"
          onClick={handleClick}
          disabled={disabledReason !== null || startMutation.isPending}
          title={disabledReason ?? undefined}
        >
          {startMutation.isPending ? (
            <>
              <RefreshCw className="mr-1.5 h-3.5 w-3.5 animate-spin" />
              Starting…
            </>
          ) : (
            <>
              Dry-run
              {selectionCount && selectionCount > 0 ? (
                <span className="ml-1.5 rounded-md bg-glass-strong px-1.5 py-0.5 text-[10px]">
                  {selectionCount.toLocaleString()}
                </span>
              ) : null}
            </>
          )}
        </Button>
        <Button
          size="sm"
          disabled
          title="Import lands in fase 4."
          className="h-9 cursor-not-allowed opacity-50"
        >
          Import
        </Button>
      </div>
    </div>
  );
}

function SelectionBadge({
  count,
  matchingMode,
}: {
  count: number | null;
  matchingMode: boolean;
}) {
  if (matchingMode) {
    return (
      <Badge className="border border-violet-400/30 bg-violet-500/10 text-xs font-normal text-violet-200">
        {count !== null ? `${count.toLocaleString()} (all matching)` : "all matching"}
      </Badge>
    );
  }
  if (count && count > 0) {
    return (
      <Badge className="border border-sky-400/30 bg-sky-500/10 text-xs font-normal text-sky-200">
        {count.toLocaleString()} selected
      </Badge>
    );
  }
  return (
    <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
      Nothing selected
    </Badge>
  );
}

type MultiSelectOption = { id: number; label: string };

function MultiSelectFilter({
  label,
  icon,
  options,
  selected,
  loading,
  onChange,
}: {
  label: string;
  icon: React.ReactNode;
  options: MultiSelectOption[];
  selected: number[];
  loading: boolean;
  onChange: (ids: number[]) => void;
}) {
  const selectedSet = useMemo(() => new Set(selected), [selected]);
  const selectedLabel = useMemo(() => {
    if (selected.length === 0) return "all";
    if (selected.length === 1) {
      const match = options.find((o) => o.id === selected[0]);
      return match?.label ?? `1 selected`;
    }
    return `${selected.length} selected`;
  }, [options, selected]);

  function toggleId(id: number) {
    const next = new Set(selectedSet);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    onChange([...next]);
  }

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button
          size="sm"
          variant="ghost"
          className="h-9 gap-1.5 border border-glass-strong bg-glass text-xs"
        >
          {icon}
          <span className="text-muted-foreground">{label}:</span>
          <span className="text-foreground">{selectedLabel}</span>
          <ChevronDown className="h-3 w-3 text-muted-foreground/60" />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        align="start"
        className="max-h-72 w-64 overflow-y-auto border-glass-strong bg-popover/95 p-2 backdrop-blur"
      >
        {loading ? (
          <div className="space-y-1">
            <Skeleton className="h-5 w-full bg-glass" />
            <Skeleton className="h-5 w-full bg-glass" />
            <Skeleton className="h-5 w-full bg-glass" />
          </div>
        ) : options.length === 0 ? (
          <p className="px-2 py-3 text-xs text-muted-foreground/60">None available.</p>
        ) : (
          <div className="space-y-0.5">
            {selected.length > 0 && (
              <button
                type="button"
                onClick={() => onChange([])}
                className="mb-1 w-full rounded px-2 py-1 text-left text-[11px] text-muted-foreground hover:bg-glass-hover hover:text-foreground"
              >
                Clear selection
              </button>
            )}
            {options.map((opt) => {
              const checked = selectedSet.has(opt.id);
              return (
                <label
                  key={opt.id}
                  className={cn(
                    "flex cursor-pointer items-center gap-2 rounded px-2 py-1 text-xs",
                    checked ? "bg-glass text-foreground" : "text-muted-foreground hover:bg-glass-hover",
                  )}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggleId(opt.id)}
                    className="h-3.5 w-3.5 rounded border-glass-strong bg-glass accent-violet-500"
                  />
                  <span className="truncate">{opt.label}</span>
                </label>
              );
            })}
          </div>
        )}
      </PopoverContent>
    </Popover>
  );
}

function ResultTable({
  page,
  loading,
  error,
  selectedIds,
  selectAllMatching,
  onToggleRow,
  groups,
  states,
}: {
  page: ZammadTicketSearchPage | undefined;
  loading: boolean;
  error: Error | null;
  selectedIds: Set<number>;
  selectAllMatching: boolean;
  onToggleRow: (id: number) => void;
  groups: { id: number; name: string }[];
  states: { id: number; name: string }[];
}) {
  // Zammad 7's search payload returns only IDs for relations — the
  // group/state names land on the ticket-row as null. We resolve those
  // client-side against the already-cached picker dropdowns so the
  // table renders meaningful labels without an extra round-trip.
  const groupNameById = useMemo(() => {
    const m = new Map<number, string>();
    for (const g of groups) m.set(g.id, g.name);
    return m;
  }, [groups]);
  const stateNameById = useMemo(() => {
    const m = new Map<number, string>();
    for (const s of states) m.set(s.id, s.name);
    return m;
  }, [states]);
  if (loading && !page) {
    return <Skeleton className="h-48 w-full bg-glass" />;
  }
  if (error) {
    return (
      <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] p-3 text-xs text-rose-200">
        Search failed — {apiErrorMessage(error) ?? error.message}
      </div>
    );
  }
  if (!page || page.items.length === 0) {
    return (
      <div className="rounded-md border border-glass-strong bg-glass p-4 text-xs text-muted-foreground/70">
        No matches. Adjust the filters or run the search again.
      </div>
    );
  }
  return (
    <div className="overflow-hidden rounded-md border border-glass-strong bg-glass">
      <table className="w-full text-xs">
        <thead className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
          <tr className="border-b border-glass">
            <th className="w-8 px-2 py-2"></th>
            <th className="px-2 py-2 text-left">#</th>
            <th className="px-2 py-2 text-left">Title</th>
            <th className="px-2 py-2 text-left">Customer</th>
            <th className="px-2 py-2 text-left">Group</th>
            <th className="px-2 py-2 text-left">State</th>
            <th className="px-2 py-2 text-right">Updated</th>
          </tr>
        </thead>
        <tbody>
          {page.items.map((t) => {
            const checked = selectAllMatching || selectedIds.has(t.id);
            return (
              <tr
                key={t.id}
                className={cn(
                  "border-b border-glass last:border-b-0",
                  checked ? "bg-violet-500/[0.05]" : "",
                  "hover:bg-glass-hover",
                )}
              >
                <td className="px-2 py-1.5 align-top">
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => onToggleRow(t.id)}
                    disabled={selectAllMatching}
                    className="h-3.5 w-3.5 rounded border-glass-strong bg-glass accent-violet-500 disabled:opacity-40"
                  />
                </td>
                <td className="px-2 py-1.5 align-top whitespace-nowrap font-mono text-muted-foreground">
                  {t.number !== null ? `#${t.number}` : <span className="opacity-50">—</span>}
                </td>
                <td className="px-2 py-1.5 align-top text-foreground">
                  <span className="line-clamp-1">{t.title}</span>
                  {t.articleCount !== null && t.articleCount > 0 ? (
                    <span className="ml-2 text-[10px] text-muted-foreground/60">
                      ({t.articleCount} {t.articleCount === 1 ? "article" : "articles"})
                    </span>
                  ) : null}
                </td>
                <td className="px-2 py-1.5 align-top text-muted-foreground">
                  {t.customerEmail ??
                    t.customerName ??
                    (t.customerId !== null ? (
                      <span className="font-mono text-muted-foreground/60">#{t.customerId}</span>
                    ) : (
                      <span className="opacity-50">—</span>
                    ))}
                </td>
                <td className="px-2 py-1.5 align-top text-muted-foreground">
                  {t.groupName ??
                    (t.groupId !== null ? groupNameById.get(t.groupId) : null) ?? (
                      <span className="opacity-50">—</span>
                    )}
                </td>
                <td className="px-2 py-1.5 align-top text-muted-foreground">
                  {t.stateName ??
                    (t.stateId !== null ? stateNameById.get(t.stateId) : null) ?? (
                      <span className="opacity-50">—</span>
                    )}
                </td>
                <td className="px-2 py-1.5 align-top whitespace-nowrap text-right text-muted-foreground tabular-nums">
                  {formatRelative(t.updatedAt)}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function formatRelative(iso: string | null): string {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "2-digit",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

function FieldOrSkeleton({
  entry,
  queryKey,
  label,
  hint,
}: {
  entry: SettingEntry | undefined;
  queryKey: readonly unknown[];
  label: string;
  hint?: string;
}) {
  if (!entry) {
    return (
      <div className="flex items-center justify-between gap-4 py-3 text-xs text-muted-foreground/60">
        <span>{label}</span>
        <span className="italic">missing</span>
      </div>
    );
  }
  return <SettingField entry={entry} queryKey={queryKey} label={label} hint={hint} />;
}
