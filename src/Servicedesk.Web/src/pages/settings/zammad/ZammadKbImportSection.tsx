import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlertCircle,
  BookOpen,
  CheckCircle2,
  ChevronRight,
  FolderTree,
  Loader2,
  Play,
  Search,
  Sparkles,
  X,
} from "lucide-react";
import {
  apiErrorMessage,
  zammadKbImportApi,
  type ZammadKbImportRunSummary,
  type ZammadKbProposal,
  type ZammadKbProposalNode,
  type ZammadKbPickerItem,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

const RUNS_QK = ["integrations", "zammad", "kb-import", "runs"] as const;
const KBS_QK = ["integrations", "zammad", "kb-import", "knowledge-bases"] as const;

function activeRunSlot(runs: ZammadKbImportRunSummary[] | undefined) {
  return runs?.find((r) =>
    ["Pending", "Proposing", "AwaitingApproval", "Approved", "Importing"].includes(r.status),
  );
}

export function ZammadKbImportSection() {
  const qc = useQueryClient();
  const runs = useQuery({
    queryKey: RUNS_QK,
    queryFn: () => zammadKbImportApi.listRuns(25),
    refetchInterval: 5000,
  });
  const active = activeRunSlot(runs.data?.items);
  const startRun = useMutation({
    mutationFn: () => zammadKbImportApi.startRun(),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: RUNS_QK });
      toast.success("New KB import started — pick a knowledge base.");
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not start KB import."),
  });

  return (
    <section className="rounded-xl border border-white/10 bg-white/[0.03] p-6">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <BookOpen className="h-4 w-4 text-purple-300" />
            <h2 className="text-base font-semibold tracking-tight text-white">
              Knowledge base import
            </h2>
            <Badge variant="outline" className="border-purple-400/30 bg-purple-500/10 text-purple-200">
              v0.0.43
            </Badge>
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            Migrate Zammad knowledge base categories + articles into the local KB. The
            importer proposes a section tree first, then runs the article migration with
            inline image preservation.
          </p>
        </div>
        {!active && (
          <Button onClick={() => startRun.mutate()} disabled={startRun.isPending}>
            {startRun.isPending ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <Sparkles className="mr-2 h-4 w-4" />
            )}
            New KB import
          </Button>
        )}
      </header>

      {active ? (
        <ActiveRunCard runId={active.id} status={active.status} />
      ) : (
        <p className="rounded-lg border border-dashed border-white/10 bg-white/[0.02] p-4 text-sm text-muted-foreground">
          No active import. Click <em>New KB import</em> to begin.
        </p>
      )}

      <RunsHistoryTable runs={runs.data?.items ?? []} loading={runs.isLoading} />
    </section>
  );
}

function ActiveRunCard({ runId, status }: { runId: string; status: string }) {
  // Stepper steps follow the lifecycle: pick KB → review proposal →
  // pick articles → progress.
  if (status === "Pending" || status === "Proposing") {
    return <KbPickerStep runId={runId} />;
  }
  if (status === "AwaitingApproval") {
    return <ProposalReviewStep runId={runId} />;
  }
  if (status === "Approved") {
    return <ArticlePickerStep runId={runId} />;
  }
  return <ProgressStep runId={runId} />;
}

// ---- Step 1: KB picker -----------------------------------------------

function KbPickerStep({ runId }: { runId: string }) {
  const qc = useQueryClient();
  const kbs = useQuery({
    queryKey: KBS_QK,
    queryFn: () => zammadKbImportApi.listKnowledgeBases(),
  });
  const proposal = useMutation({
    mutationFn: (kbId: number) => zammadKbImportApi.buildProposal(runId, kbId),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: RUNS_QK });
      toast.success("Proposal built — review the section tree below.");
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not build proposal."),
  });

  if (kbs.isLoading) {
    return <Loader />;
  }
  if (kbs.error) {
    return <ErrorBanner message={apiErrorMessage(kbs.error) ?? "Failed to list knowledge bases."} />;
  }
  const items = kbs.data?.items ?? [];
  if (items.length === 0) {
    return <ErrorBanner message="Zammad returned no knowledge bases — verify the source install has KB content." />;
  }

  return (
    <div className="space-y-3">
      <StepHeader step={1} title="Choose a Zammad knowledge base" />
      <ul className="divide-y divide-white/5 rounded-lg border border-white/10 bg-white/[0.02]">
        {items.map((kb) => (
          <li key={kb.id} className="flex items-center justify-between px-4 py-3">
            <div>
              <div className="font-medium text-white">{kb.name}</div>
              <div className="text-xs text-muted-foreground">
                ID {kb.id} · {kb.categoryCount} categories · {kb.answerCount} articles
                {kb.active ? "" : " · inactive"}
              </div>
            </div>
            <Button
              size="sm"
              onClick={() => proposal.mutate(kb.id)}
              disabled={proposal.isPending}
            >
              {proposal.isPending ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
              Build proposal
            </Button>
          </li>
        ))}
      </ul>
    </div>
  );
}

// ---- Step 2: proposal review -----------------------------------------

function ProposalReviewStep({ runId }: { runId: string }) {
  const qc = useQueryClient();
  const proposalQ = useQuery({
    queryKey: ["zammad-kb-import", "proposal", runId],
    queryFn: () => zammadKbImportApi.getProposal(runId),
  });
  const [draft, setDraft] = useState<ZammadKbProposalNode[] | null>(null);
  useEffect(() => {
    if (proposalQ.data) setDraft(proposalQ.data.nodes);
  }, [proposalQ.data]);

  const save = useMutation({
    mutationFn: (nodes: ZammadKbProposalNode[]) => zammadKbImportApi.saveDecisions(runId, nodes),
    onSuccess: () => toast.success("Decisions saved."),
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not save decisions."),
  });
  const apply = useMutation({
    mutationFn: async () => {
      // Save before applying so an in-flight edit never gets lost.
      if (draft) await zammadKbImportApi.saveDecisions(runId, draft);
      return zammadKbImportApi.applyProposal(runId);
    },
    onSuccess: (res) => {
      toast.success(`Created ${res.mappingCount} section mapping(s).`);
      void qc.invalidateQueries({ queryKey: RUNS_QK });
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Apply failed."),
  });

  if (proposalQ.isLoading) return <Loader />;
  if (proposalQ.error) {
    return <ErrorBanner message={apiErrorMessage(proposalQ.error) ?? "Failed to load proposal."} />;
  }
  if (!proposalQ.data || !draft) return <Loader />;
  const proposal: ZammadKbProposal = proposalQ.data;

  const updateNode = (idx: number, patch: Partial<ZammadKbProposalNode>) => {
    setDraft((cur) => {
      if (!cur) return cur;
      const next = [...cur];
      next[idx] = { ...next[idx], ...patch };
      return next;
    });
  };

  return (
    <div className="space-y-4">
      <StepHeader
        step={2}
        title={`Review section proposal — ${proposal.knowledgeBaseName}`}
        subtitle={`${proposal.nodes.length} sections · ${proposal.totalAnswerCount} articles total`}
      />
      <ul className="space-y-2">
        {draft.map((node, idx) => (
          <li
            key={node.zammadCategoryId}
            className="rounded-lg border border-white/10 bg-white/[0.02] p-3"
            style={{ marginLeft: `${node.depth * 16}px` }}
          >
            <div className="flex flex-wrap items-center gap-2">
              <FolderTree className="h-3.5 w-3.5 text-muted-foreground" />
              <Input
                value={node.proposedTitle}
                onChange={(e) => updateNode(idx, { proposedTitle: e.target.value })}
                className="h-8 max-w-xs"
              />
              <Input
                value={node.proposedSlug}
                onChange={(e) => updateNode(idx, { proposedSlug: e.target.value })}
                className="h-8 max-w-[160px] font-mono text-xs"
              />
              <span className="text-xs text-muted-foreground">
                {node.answerCount} articles
              </span>
              <div className="ml-auto flex gap-1">
                {(["create", "merge", "skip"] as const).map((action) => (
                  <button
                    key={action}
                    type="button"
                    onClick={() => updateNode(idx, { action })}
                    className={cn(
                      "rounded px-2 py-1 text-xs font-medium capitalize transition",
                      node.action === action
                        ? "bg-purple-500/20 text-purple-200 ring-1 ring-purple-400/40"
                        : "bg-white/[0.04] text-muted-foreground hover:bg-white/[0.08]",
                    )}
                  >
                    {action}
                  </button>
                ))}
              </div>
            </div>
          </li>
        ))}
      </ul>
      <div className="flex justify-end gap-2 pt-2">
        <Button
          variant="outline"
          onClick={() => draft && save.mutate(draft)}
          disabled={save.isPending}
        >
          {save.isPending ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
          Save draft
        </Button>
        <Button onClick={() => apply.mutate()} disabled={apply.isPending}>
          {apply.isPending ? (
            <Loader2 className="mr-2 h-4 w-4 animate-spin" />
          ) : (
            <CheckCircle2 className="mr-2 h-4 w-4" />
          )}
          Apply proposal
        </Button>
      </div>
    </div>
  );
}

// ---- Step 3: article picker -------------------------------------------

function ArticlePickerStep({ runId }: { runId: string }) {
  const qc = useQueryClient();
  const [status, setStatus] = useState<string>("");
  const [search, setSearch] = useState<string>("");
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const picker = useQuery({
    queryKey: ["zammad-kb-import", "picker", runId, status, search],
    queryFn: () =>
      zammadKbImportApi.picker(runId, {
        status: status || undefined,
        freeText: search || undefined,
        pageSize: 200,
      }),
  });
  const start = useMutation({
    mutationFn: () => zammadKbImportApi.startImport(runId, Array.from(selected)),
    onSuccess: () => {
      toast.success("Article import started.");
      void qc.invalidateQueries({ queryKey: RUNS_QK });
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not start article import."),
  });

  const items = picker.data?.items ?? [];
  const allSelected = items.length > 0 && items.every((it) => selected.has(it.zammadAnswerId));

  const toggle = (id: number) => {
    setSelected((cur) => {
      const next = new Set(cur);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };
  const toggleAll = () => {
    setSelected((cur) => {
      if (allSelected) {
        const next = new Set(cur);
        items.forEach((it) => next.delete(it.zammadAnswerId));
        return next;
      }
      const next = new Set(cur);
      items.forEach((it) => next.add(it.zammadAnswerId));
      return next;
    });
  };

  return (
    <div className="space-y-3">
      <StepHeader
        step={3}
        title="Pick articles to migrate"
        subtitle={`Selected ${selected.size} of ${picker.data?.total ?? 0} matching`}
      />
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative">
          <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search title…"
            className="h-8 pl-7"
          />
        </div>
        <select
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          className="h-8 rounded-md border border-white/10 bg-white/[0.04] px-2 text-sm text-white"
        >
          <option value="">All statuses</option>
          <option value="Draft">Draft</option>
          <option value="Internal">Internal</option>
          <option value="Published">Published</option>
          <option value="Archived">Archived</option>
        </select>
        <Button
          variant="outline"
          size="sm"
          onClick={toggleAll}
          disabled={items.length === 0}
        >
          {allSelected ? "Clear page" : "Select page"}
        </Button>
        <div className="ml-auto">
          <Button onClick={() => start.mutate()} disabled={selected.size === 0 || start.isPending}>
            {start.isPending ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <Play className="mr-2 h-4 w-4" />
            )}
            Start import ({selected.size})
          </Button>
        </div>
      </div>
      <ArticleList items={items} selected={selected} onToggle={toggle} loading={picker.isLoading} />
    </div>
  );
}

function ArticleList({
  items,
  selected,
  onToggle,
  loading,
}: {
  items: ZammadKbPickerItem[];
  selected: Set<number>;
  onToggle: (id: number) => void;
  loading: boolean;
}) {
  if (loading) return <Loader />;
  if (items.length === 0) {
    return (
      <p className="rounded-lg border border-dashed border-white/10 p-4 text-center text-sm text-muted-foreground">
        No articles match the current filter.
      </p>
    );
  }
  return (
    <ul className="max-h-[400px] divide-y divide-white/5 overflow-y-auto rounded-lg border border-white/10 bg-white/[0.02]">
      {items.map((item) => (
        <li
          key={item.zammadAnswerId}
          className="flex items-center gap-3 px-3 py-2 hover:bg-white/[0.03]"
        >
          <input
            type="checkbox"
            checked={selected.has(item.zammadAnswerId)}
            onChange={() => onToggle(item.zammadAnswerId)}
            className="h-4 w-4 cursor-pointer accent-purple-500"
          />
          <div className="min-w-0 flex-1">
            <div className="truncate text-sm font-medium text-white">{item.title}</div>
            <div className="truncate text-xs text-muted-foreground">
              {item.categoryTitle ?? "—"} · id {item.zammadAnswerId}
              {item.promoted ? " · promoted" : ""}
              {!item.hasTranslation ? " · no nl-BE translation" : ""}
            </div>
          </div>
          <Badge
            variant="outline"
            className={cn(
              "text-xs",
              item.status === "Published" && "border-emerald-400/30 bg-emerald-500/10 text-emerald-200",
              item.status === "Internal" && "border-sky-400/30 bg-sky-500/10 text-sky-200",
              item.status === "Draft" && "border-amber-400/30 bg-amber-500/10 text-amber-200",
              item.status === "Archived" && "border-white/15 bg-white/[0.06] text-muted-foreground",
            )}
          >
            {item.status}
          </Badge>
        </li>
      ))}
    </ul>
  );
}

// ---- Step 4: progress + records --------------------------------------

function ProgressStep({ runId }: { runId: string }) {
  const qc = useQueryClient();
  const run = useQuery({
    queryKey: ["zammad-kb-import", "run", runId],
    queryFn: () => zammadKbImportApi.getRun(runId),
    refetchInterval: (q) => {
      const s = q.state.data?.summary.status;
      if (s === "Importing" || s === "Approved" || s === "Pending") return 2000;
      return false;
    },
  });
  const cancel = useMutation({
    mutationFn: () => zammadKbImportApi.cancel(runId),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: RUNS_QK });
      toast.success("Cancellation requested.");
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not cancel."),
  });
  if (run.isLoading) return <Loader />;
  const summary = run.data?.summary;
  if (!summary) return <ErrorBanner message="Run not found." />;
  const t = summary.totals;
  const progress = t.plannedTotal && t.plannedTotal > 0
    ? Math.min(100, Math.round((t.processed / t.plannedTotal) * 100))
    : 0;
  return (
    <div className="space-y-3">
      <StepHeader
        step={4}
        title={`Import ${summary.status.toLowerCase()}`}
        subtitle={summary.sourceKbName ?? undefined}
      />
      <div className="rounded-lg border border-white/10 bg-white/[0.02] p-3">
        <div className="mb-2 flex items-center justify-between text-sm">
          <span className="font-medium text-white">
            {t.processed} / {t.plannedTotal ?? "?"} processed
          </span>
          <span className="text-xs text-muted-foreground">{progress}%</span>
        </div>
        <div className="h-2 w-full overflow-hidden rounded-full bg-white/[0.06]">
          <div
            className="h-full bg-gradient-to-r from-purple-500 to-sky-400 transition-[width] duration-500"
            style={{ width: `${progress}%` }}
          />
        </div>
        <div className="mt-3 grid grid-cols-2 gap-2 text-xs text-muted-foreground sm:grid-cols-4">
          <Stat label="Imported" value={t.imported} tone="success" />
          <Stat label="Already imported" value={t.alreadyImported} />
          <Stat label="No section" value={t.skippedNoSectionMapping} tone="warn" />
          <Stat label="No translation" value={t.skippedNoTranslation} tone="warn" />
          <Stat label="Section skipped" value={t.skippedSectionSkipped} />
          <Stat label="Failed" value={t.failed} tone="danger" />
        </div>
        {summary.errorMessage ? (
          <p className="mt-3 rounded border border-red-400/30 bg-red-500/10 p-2 text-xs text-red-200">
            {summary.errorMessage}
          </p>
        ) : null}
      </div>
      {(summary.status === "Importing" || summary.status === "Pending") && (
        <div className="flex justify-end">
          <Button
            variant="outline"
            size="sm"
            onClick={() => cancel.mutate()}
            disabled={cancel.isPending}
          >
            <X className="mr-2 h-3.5 w-3.5" />
            Cancel run
          </Button>
        </div>
      )}
    </div>
  );
}

function Stat({
  label,
  value,
  tone,
}: {
  label: string;
  value: number;
  tone?: "success" | "warn" | "danger";
}) {
  const colour =
    tone === "success"
      ? "text-emerald-300"
      : tone === "warn"
      ? "text-amber-300"
      : tone === "danger"
      ? "text-red-300"
      : "text-white";
  return (
    <div>
      <div className={cn("text-lg font-semibold tabular-nums", colour)}>{value}</div>
      <div>{label}</div>
    </div>
  );
}

// ---- Runs history ----------------------------------------------------

function RunsHistoryTable({
  runs,
  loading,
}: {
  runs: ZammadKbImportRunSummary[];
  loading: boolean;
}) {
  if (loading) return null;
  if (runs.length <= 1) return null;
  // Drop the active run (already rendered on top).
  const history = runs.filter((r) =>
    ["Completed", "Failed", "Cancelled"].includes(r.status),
  );
  if (history.length === 0) return null;
  return (
    <div className="mt-6">
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        Past runs
      </h3>
      <table className="w-full text-sm">
        <thead className="text-xs text-muted-foreground">
          <tr>
            <th className="py-1 text-left">Started</th>
            <th className="py-1 text-left">KB</th>
            <th className="py-1 text-left">Status</th>
            <th className="py-1 text-right">Imported</th>
            <th className="py-1 text-right">Failed</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-white/5">
          {history.map((r) => (
            <tr key={r.id}>
              <td className="py-1.5 text-xs text-muted-foreground">
                {new Date(r.startedUtc).toLocaleString()}
              </td>
              <td className="py-1.5">{r.sourceKbName ?? "—"}</td>
              <td className="py-1.5">
                <Badge
                  variant="outline"
                  className={cn(
                    "text-xs",
                    r.status === "Completed" && "border-emerald-400/30 bg-emerald-500/10 text-emerald-200",
                    r.status === "Failed" && "border-red-400/30 bg-red-500/10 text-red-200",
                    r.status === "Cancelled" && "border-white/15 bg-white/[0.06] text-muted-foreground",
                  )}
                >
                  {r.status}
                </Badge>
              </td>
              <td className="py-1.5 text-right tabular-nums">{r.totals.imported}</td>
              <td className="py-1.5 text-right tabular-nums">{r.totals.failed}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---- shared primitives ------------------------------------------------

function StepHeader({
  step,
  title,
  subtitle,
}: {
  step: number;
  title: string;
  subtitle?: string;
}) {
  return (
    <div className="flex items-center gap-3">
      <div className="flex h-7 w-7 items-center justify-center rounded-full bg-purple-500/15 text-xs font-semibold text-purple-200">
        {step}
      </div>
      <div>
        <div className="text-sm font-semibold text-white">{title}</div>
        {subtitle ? <div className="text-xs text-muted-foreground">{subtitle}</div> : null}
      </div>
      <ChevronRight className="ml-auto h-4 w-4 text-muted-foreground" />
    </div>
  );
}

function Loader() {
  return (
    <div className="flex items-center justify-center rounded-lg border border-white/10 bg-white/[0.02] p-8 text-sm text-muted-foreground">
      <Loader2 className="mr-2 h-4 w-4 animate-spin" />
      Loading…
    </div>
  );
}

function ErrorBanner({ message }: { message: string }) {
  return (
    <p className="flex items-start gap-2 rounded-lg border border-red-400/30 bg-red-500/10 p-3 text-sm text-red-200">
      <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
      <span>{message}</span>
    </p>
  );
}
