import { formatDateTime } from "@/lib/dateFormat";
import { useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import {
  ArrowLeft,
  BarChart3,
  ChevronLeft,
  ChevronRight,
  ListChecks,
  Loader2,
  MailQuestion,
  MessageSquareText,
  Trophy,
  Users,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";
import {
  surveyResultsApi,
  surveysApi,
  type RatingConfig,
  type ChoiceConfig,
  type SurveyResponseCommentFilter,
  type SurveyResponseDetail,
  type SurveyResponseSort,
  type SurveyResultsAggregate,
} from "@/lib/surveys-api";

export function SurveyResultsPage({ surveyId }: { surveyId: string }) {
  const navigate = useNavigate();
  const [openInvitationId, setOpenInvitationId] = useState<string | null>(null);

  const surveyQ = useQuery({
    queryKey: ["surveys", "detail", surveyId],
    queryFn: () => surveysApi.get(surveyId),
  });
  const aggQ = useQuery({
    queryKey: ["surveys", "results", surveyId],
    queryFn: () => surveyResultsApi.aggregate(surveyId),
  });
  const invitationsQ = useQuery({
    queryKey: ["surveys", "results", surveyId, "invitations"],
    queryFn: () => surveyResultsApi.invitations(surveyId, undefined, 100),
  });

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between gap-3">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">
            {surveyQ.data?.name ?? "Survey results"}
          </h1>
          <p className="text-sm text-muted-foreground">
            Aggregated answers + per-agent breakdown. Click a "Submitted"
            badge below to inspect a single response.
          </p>
        </div>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => navigate({ to: "/settings/surveys" })}
        >
          <ArrowLeft className="h-4 w-4" />
          Back to list
        </Button>
      </header>

      {aggQ.isLoading && <Skeleton className="h-32 w-full" />}
      {aggQ.data && <StatsTiles agg={aggQ.data} />}

      {aggQ.data && aggQ.data.agentLeaderboard.length > 0 && (
        <section className="rounded-lg border border-glass-strong bg-glass p-5">
          <header className="mb-3 flex items-center gap-2 text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            <Trophy className="h-4 w-4 text-primary" />
            Agent leaderboard
          </header>
          <p className="mb-3 text-xs text-muted-foreground">
            Average across every numeric per-agent sub-question. Non-numeric
            answers count toward the response total but not the average.
          </p>
          <ul className="flex flex-col gap-1.5">
            {aggQ.data.agentLeaderboard.map((row, i) => (
              <li
                key={row.agentUserId}
                className="flex items-center justify-between gap-4 rounded-md border border-glass-strong bg-glass px-3 py-2"
              >
                <div className="flex items-center gap-3">
                  <span
                    className={cn(
                      "flex h-7 w-7 items-center justify-center rounded-full text-xs font-medium",
                      i === 0
                        ? "bg-amber-500/20 text-amber-200"
                        : i === 1
                          ? "bg-slate-400/20 text-slate-200"
                          : i === 2
                            ? "bg-orange-700/20 text-orange-200"
                            : "bg-glass-strong text-muted-foreground",
                    )}
                  >
                    {i + 1}
                  </span>
                  <span className="text-sm font-medium text-foreground">
                    {row.displayName}
                  </span>
                </div>
                <div className="flex items-center gap-4 text-xs text-muted-foreground">
                  <span>
                    {row.responseCount} response
                    {row.responseCount === 1 ? "" : "s"}
                  </span>
                  <span className="rounded-full bg-primary/15 px-2.5 py-0.5 text-sm font-medium text-primary-foreground">
                    {row.averageRating !== null
                      ? row.averageRating.toFixed(2)
                      : "—"}
                  </span>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}

      {aggQ.data && aggQ.data.questionAggregates.length > 0 && (
        <section className="rounded-lg border border-glass-strong bg-glass p-5">
          <header className="mb-3 flex items-center gap-2 text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            <BarChart3 className="h-4 w-4 text-primary" />
            Survey questions
          </header>
          <div className="flex flex-col gap-4">
            {aggQ.data.questionAggregates.map((q) => (
              <div
                key={q.questionId}
                className="rounded-md border border-glass-strong bg-glass p-3"
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="text-sm font-medium text-foreground">
                    {q.label}
                  </span>
                  <span className="text-[10px] uppercase tracking-wider text-muted-foreground">
                    {q.type}
                  </span>
                </div>
                <p className="mt-1 text-xs text-muted-foreground">
                  {q.answerCount} answer{q.answerCount === 1 ? "" : "s"}
                  {q.averageNumeric !== null
                    ? ` · average ${q.averageNumeric.toFixed(2)}`
                    : ""}
                </p>
                {q.tally && Object.keys(q.tally).length > 0 && (
                  <TallyBars tally={q.tally} />
                )}
              </div>
            ))}
          </div>
        </section>
      )}

      {aggQ.data && aggQ.data.agentQuestionAggregates.length > 0 && (
        <PerAgentQuestionSection
          rows={aggQ.data.agentQuestionAggregates}
        />
      )}

      <ResponsesSection
        surveyId={surveyId}
        onOpenResponse={setOpenInvitationId}
      />

      <section className="rounded-lg border border-glass-strong bg-glass p-5">
        <header className="mb-3 flex items-center gap-2 text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          <MailQuestion className="h-4 w-4 text-primary" />
          Recent invitations
        </header>
        {invitationsQ.isLoading && <Skeleton className="h-12 w-full" />}
        {invitationsQ.data && invitationsQ.data.length === 0 && (
          <p className="text-sm text-muted-foreground">
            No invitations sent yet.
          </p>
        )}
        {invitationsQ.data && invitationsQ.data.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-[10px] uppercase tracking-wider text-muted-foreground/60">
                <tr>
                  <th className="text-left font-medium">Contact</th>
                  <th className="text-left font-medium">Company</th>
                  <th className="text-left font-medium">Ticket</th>
                  <th className="text-left font-medium">Sent</th>
                  <th className="text-left font-medium">Status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-glass">
                {invitationsQ.data.map((inv) => (
                  <tr key={inv.id}>
                    <td className="py-2 text-foreground">
                      <div className="font-medium">
                        {inv.contactName ?? inv.sentToEmail}
                      </div>
                      {inv.contactName && (
                        <div className="text-xs text-muted-foreground">
                          {inv.sentToEmail}
                        </div>
                      )}
                    </td>
                    <td className="py-2 text-muted-foreground">
                      {inv.companyName ?? "—"}
                    </td>
                    <td className="py-2 text-muted-foreground">
                      #{inv.ticketNumber}{" "}
                      <span className="text-muted-foreground/60">
                        {inv.ticketSubject}
                      </span>
                    </td>
                    <td className="py-2 text-muted-foreground">
                      {formatDateTime(inv.sentUtc)}
                    </td>
                    <td className="py-2">
                      <StatusBadge
                        status={inv.status}
                        onOpenResponse={
                          inv.status === "Submitted"
                            ? () => setOpenInvitationId(inv.id)
                            : undefined
                        }
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <ResponseDetailDialog
        invitationId={openInvitationId}
        onClose={() => setOpenInvitationId(null)}
      />
    </div>
  );
}

// ============================================================
// Responses — full, paged list of submitted responses
// ============================================================

const RESPONSES_PAGE_SIZE = 50;

const SORT_OPTIONS: Array<{ value: SurveyResponseSort; label: string }> = [
  { value: "SubmittedDesc", label: "Newest first" },
  { value: "SubmittedAsc", label: "Oldest first" },
  { value: "ScoreAsc", label: "Score: low to high" },
  { value: "ScoreDesc", label: "Score: high to low" },
];

const COMMENT_FILTERS: Array<{
  value: SurveyResponseCommentFilter;
  label: string;
}> = [
  { value: "All", label: "All" },
  { value: "WithComment", label: "With comment" },
  { value: "WithoutComment", label: "Without comment" },
];

function ResponsesSection({
  surveyId,
  onOpenResponse,
}: {
  surveyId: string;
  onOpenResponse: (invitationId: string) => void;
}) {
  const [sort, setSort] = useState<SurveyResponseSort>("SubmittedDesc");
  const [comment, setComment] = useState<SurveyResponseCommentFilter>("All");
  const [offset, setOffset] = useState(0);

  const responsesQ = useQuery({
    queryKey: [
      "surveys",
      "results",
      surveyId,
      "responses",
      sort,
      comment,
      offset,
    ],
    queryFn: () =>
      surveyResultsApi.responses(surveyId, {
        sort,
        comment,
        offset,
        limit: RESPONSES_PAGE_SIZE,
      }),
    placeholderData: (prev) => prev,
  });

  const total = responsesQ.data?.total ?? 0;
  const items = responsesQ.data?.items ?? [];
  const from = total === 0 ? 0 : offset + 1;
  const to = Math.min(offset + items.length, total);

  return (
    <section className="sd-survey-responses rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2 text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          <ListChecks className="h-4 w-4 text-primary" />
          Responses
          {responsesQ.data && (
            <span className="rounded-full bg-glass-strong px-2 py-0.5 text-[10px] normal-case tracking-normal text-muted-foreground">
              {total}
            </span>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <div
            role="radiogroup"
            aria-label="Filter on comment"
            className="flex items-center rounded-md border border-glass-strong bg-glass p-0.5"
          >
            {COMMENT_FILTERS.map((f) => (
              <button
                key={f.value}
                type="button"
                role="radio"
                aria-checked={comment === f.value}
                onClick={() => {
                  setComment(f.value);
                  setOffset(0);
                }}
                className={cn(
                  "rounded px-2.5 py-1 text-xs transition",
                  comment === f.value
                    ? "bg-primary/15 font-medium text-foreground"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {f.label}
              </button>
            ))}
          </div>
          <Select
            value={sort}
            onValueChange={(v) => {
              setSort(v as SurveyResponseSort);
              setOffset(0);
            }}
          >
            <SelectTrigger
              className="h-8 w-48 text-xs"
              aria-label="Sort responses"
            >
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
      </header>
      <p className="mb-3 text-xs text-muted-foreground">
        Every submitted response. The score is the average of all rating and
        NPS answers, each scaled to 0–100% of its own range. Click a row to
        open the full response.
      </p>

      {responsesQ.isLoading && <Skeleton className="h-24 w-full" />}
      {responsesQ.isError && (
        <p className="text-sm text-destructive">Could not load responses.</p>
      )}
      {responsesQ.data && items.length === 0 && (
        <p className="text-sm text-muted-foreground">
          {comment === "All"
            ? "No responses submitted yet."
            : "No responses match this filter."}
        </p>
      )}
      {items.length > 0 && (
        <ul
          className={cn(
            "flex flex-col gap-1.5 transition-opacity",
            responsesQ.isPlaceholderData && "opacity-60",
          )}
        >
          {items.map((r) => (
            <li key={r.invitationId}>
              <button
                type="button"
                onClick={() => onOpenResponse(r.invitationId)}
                className="sd-survey-response-row flex w-full items-start gap-3 rounded-md border border-glass-strong bg-glass px-3 py-2.5 text-left transition hover:border-primary/40 hover:bg-glass-strong"
              >
                <ScorePill score={r.scorePct} />
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5">
                    <span className="truncate text-sm font-medium text-foreground">
                      {r.contactName ?? r.sentToEmail}
                      {r.companyName && (
                        <span className="font-normal text-muted-foreground">
                          {" "}
                          · {r.companyName}
                        </span>
                      )}
                    </span>
                    <span className="shrink-0 text-xs text-muted-foreground">
                      {formatDateTime(r.submittedUtc)}
                    </span>
                  </div>
                  <div className="truncate text-xs text-muted-foreground">
                    #{r.ticketNumber}{" "}
                    <span className="text-muted-foreground/70">
                      {r.ticketSubject}
                    </span>
                  </div>
                  {r.commentPreview && (
                    <div className="mt-1.5 flex items-start gap-1.5 text-xs text-foreground/80">
                      <MessageSquareText className="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                      <span className="line-clamp-2 whitespace-pre-line">
                        {r.commentPreview}
                      </span>
                    </div>
                  )}
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}

      {total > RESPONSES_PAGE_SIZE && (
        <footer className="mt-3 flex items-center justify-between gap-3 text-xs text-muted-foreground">
          <span>
            {from}–{to} of {total}
          </span>
          <div className="flex items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              disabled={offset === 0 || responsesQ.isFetching}
              onClick={() =>
                setOffset((o) => Math.max(0, o - RESPONSES_PAGE_SIZE))
              }
            >
              <ChevronLeft className="h-4 w-4" />
              Previous
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={to >= total || responsesQ.isFetching}
              onClick={() => setOffset((o) => o + RESPONSES_PAGE_SIZE)}
            >
              Next
              <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </footer>
      )}
    </section>
  );
}

function ScorePill({ score }: { score: number | null }) {
  if (score === null) {
    return (
      <span
        className="mt-0.5 w-12 shrink-0 rounded-full bg-glass-strong py-0.5 text-center text-xs text-muted-foreground"
        title="No rating or NPS answers"
      >
        —
      </span>
    );
  }
  const tone =
    score < 50
      ? "bg-rose-500/15 text-rose-700 dark:text-rose-300"
      : score < 75
        ? "bg-amber-500/15 text-amber-700 dark:text-amber-300"
        : "bg-emerald-500/15 text-emerald-700 dark:text-emerald-300";
  return (
    <span
      className={cn(
        "mt-0.5 w-12 shrink-0 rounded-full py-0.5 text-center text-xs font-semibold tabular-nums",
        tone,
      )}
      title={`Score ${score.toFixed(1)}%`}
    >
      {Math.round(score)}%
    </span>
  );
}

function PerAgentQuestionSection({
  rows,
}: {
  rows: SurveyResultsAggregate["agentQuestionAggregates"];
}) {
  const byQuestion = new Map<number, typeof rows>();
  for (const r of rows) {
    const list = byQuestion.get(r.questionId) ?? [];
    list.push(r);
    byQuestion.set(r.questionId, list);
  }

  return (
    <section className="rounded-lg border border-glass-strong bg-glass p-5">
      <header className="mb-3 flex items-center gap-2 text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
        <Users className="h-4 w-4 text-primary" />
        Per-agent questions
      </header>
      <p className="mb-3 text-xs text-muted-foreground">
        Each per-agent sub-question, broken down per agent.
      </p>
      <div className="flex flex-col gap-4">
        {Array.from(byQuestion.entries()).map(([questionId, group]) => {
          const first = group[0];
          if (!first) return null;
          return (
            <div
              key={questionId}
              className="rounded-md border border-glass-strong bg-glass p-3"
            >
              <div className="flex items-center justify-between gap-2">
                <span className="text-sm font-medium text-foreground">
                  {first.label}
                </span>
                <span className="text-[10px] uppercase tracking-wider text-muted-foreground">
                  {first.type}
                </span>
              </div>
              <ul className="mt-2 flex flex-col gap-2">
                {group.map((row) => (
                  <li
                    key={`${row.questionId}:${row.agentUserId}`}
                    className="rounded-md border border-glass-strong bg-glass px-3 py-2"
                  >
                    <div className="flex items-center justify-between gap-2 text-xs text-muted-foreground">
                      <span className="text-sm font-medium text-foreground">
                        {row.agentDisplayName}
                      </span>
                      <span>
                        {row.answerCount} answer
                        {row.answerCount === 1 ? "" : "s"}
                        {row.averageNumeric !== null
                          ? ` · average ${row.averageNumeric.toFixed(2)}`
                          : ""}
                      </span>
                    </div>
                    {row.tally && Object.keys(row.tally).length > 0 && (
                      <TallyBars tally={row.tally} />
                    )}
                  </li>
                ))}
              </ul>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function StatsTiles({ agg }: { agg: SurveyResultsAggregate }) {
  const tiles: Array<{ label: string; value: string }> = [
    { label: "Sent", value: String(agg.totalSent) },
    { label: "Submitted", value: String(agg.totalSubmitted) },
    {
      label: "Response rate",
      value: `${Math.round(agg.responseRate * 100)}%`,
    },
    { label: "Expired", value: String(agg.totalExpired) },
  ];
  return (
    <section className="grid gap-3 sm:grid-cols-2 md:grid-cols-4">
      {tiles.map((t) => (
        <div
          key={t.label}
          className="rounded-lg border border-glass-strong bg-linear-to-br from-white/3 to-white/1 p-4"
        >
          <div className="text-[11px] uppercase tracking-wider text-muted-foreground">
            {t.label}
          </div>
          <div className="mt-1 text-2xl font-semibold text-foreground">
            {t.value}
          </div>
        </div>
      ))}
    </section>
  );
}

function StatusBadge({
  status,
  onOpenResponse,
}: {
  status: string;
  onOpenResponse?: () => void;
}) {
  const tint =
    status === "Submitted"
      ? "bg-emerald-500/15 text-emerald-200 hover:bg-emerald-500/25"
      : status === "Expired"
        ? "bg-orange-500/15 text-orange-200"
        : status === "Cancelled"
          ? "bg-glass-strong text-muted-foreground"
          : "bg-primary/15 text-primary-foreground";
  const className = cn(
    "rounded-full px-2 py-0.5 text-[10px] uppercase tracking-wider transition",
    tint,
    onOpenResponse && "cursor-pointer underline-offset-2 hover:underline",
  );
  if (onOpenResponse) {
    return (
      <button type="button" className={className} onClick={onOpenResponse}>
        {status}
      </button>
    );
  }
  return <span className={className}>{status}</span>;
}

function TallyBars({ tally }: { tally: Record<string, number> }) {
  const max = Math.max(...Object.values(tally));
  if (max === 0) return null;
  const entries = Object.entries(tally).sort((a, b) => {
    const an = Number(a[0]);
    const bn = Number(b[0]);
    if (!Number.isNaN(an) && !Number.isNaN(bn)) return an - bn;
    return a[0].localeCompare(b[0]);
  });
  return (
    <div className="mt-2 space-y-1.5">
      {entries.map(([key, count]) => (
        <div key={key} className="flex items-center gap-3 text-xs">
          <span className="w-20 truncate text-muted-foreground">{key}</span>
          <div className="relative flex-1 overflow-hidden rounded-full bg-glass">
            <div
              className="h-2 rounded-full bg-primary/60"
              style={{ width: `${Math.round((count / max) * 100)}%` }}
            />
          </div>
          <span className="w-10 text-right text-muted-foreground">{count}</span>
        </div>
      ))}
    </div>
  );
}

// ============================================================
// Response detail dialog
// ============================================================

function ResponseDetailDialog({
  invitationId,
  onClose,
}: {
  invitationId: string | null;
  onClose: () => void;
}) {
  const detailQ = useQuery({
    queryKey: ["surveys", "response", invitationId],
    queryFn: () => surveyResultsApi.responseDetail(invitationId!),
    enabled: invitationId !== null,
  });

  return (
    <Dialog open={invitationId !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-2xl max-h-[85vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Survey response</DialogTitle>
          <DialogDescription className="sr-only">
            Details of the customer's survey response.
          </DialogDescription>
        </DialogHeader>
        {detailQ.isLoading && (
          <div className="flex items-center gap-2 py-4 text-muted-foreground">
            <Loader2 className="h-4 w-4 animate-spin" />
            <span>Loading response…</span>
          </div>
        )}
        {detailQ.data && <ResponseDetailBody detail={detailQ.data} />}
      </DialogContent>
    </Dialog>
  );
}

function ResponseDetailBody({ detail }: { detail: SurveyResponseDetail }) {
  // The snapshot was frozen on the invitation at send time so we render
  // questions in the same order the customer answered them — instead of
  // trying to reconcile with the (possibly newer) live designer.
  const snapshot = detail.surveySnapshot as
    | {
        name?: string;
        questions?: Array<{
          id: number;
          sortOrder: number;
          type: string;
          appliesTo?: string;
          label: string;
          configJson?: string;
        }>;
      }
    | null;

  const allSnapshotQs = snapshot?.questions ?? [];
  const surveyQs = allSnapshotQs
    .filter((q) => (q.appliesTo ?? "Survey") === "Survey")
    .sort((a, b) => a.sortOrder - b.sortOrder);
  const agentQs = allSnapshotQs
    .filter((q) => q.appliesTo === "Agent")
    .sort((a, b) => a.sortOrder - b.sortOrder);

  const answerByQuestionId = new Map(detail.answers.map((a) => [a.questionId, a]));

  // Group per-agent answers by agent so each agent renders as one card.
  const agentAnswers = detail.agentAnswers;
  const agents = Array.from(
    new Map(
      agentAnswers.map((a) => [
        a.agentUserId,
        { id: a.agentUserId, name: a.agentDisplayName },
      ]),
    ).values(),
  );

  return (
    <div className="flex flex-col gap-4 text-sm">
      <header className="space-y-1">
        <div className="text-base font-semibold text-foreground">
          {snapshot?.name ?? "Survey"} — #{detail.ticketNumber}
        </div>
        <div className="text-xs text-muted-foreground">
          {detail.ticketSubject}
        </div>
        <div className="text-xs text-muted-foreground/70">
          Sent to {detail.sentToEmail} ·{" "}
          {formatDateTime(detail.sentUtc)} · submitted{" "}
          {formatDateTime(detail.submittedUtc)}
        </div>
      </header>

      {surveyQs.length > 0 && (
        <section className="flex flex-col gap-2 rounded-md border border-glass-strong bg-glass p-3">
          <header className="text-[10px] uppercase tracking-wider text-muted-foreground">
            Survey questions
          </header>
          <ul className="flex flex-col gap-2">
            {surveyQs.map((q) => {
              const ans = answerByQuestionId.get(q.id);
              return (
                <li
                  key={q.id}
                  className="rounded-md border border-glass-strong bg-glass p-2"
                >
                  <div className="text-xs text-muted-foreground">{q.label}</div>
                  <div className="text-sm text-foreground">
                    {renderAnswer(q, ans)}
                  </div>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      {agentQs.length > 0 && agents.length > 0 && (
        <section className="flex flex-col gap-2 rounded-md border border-glass-strong bg-glass p-3">
          <header className="text-[10px] uppercase tracking-wider text-muted-foreground">
            Per-agent questions
          </header>
          <div className="flex flex-col gap-3">
            {agents.map((agent) => {
              const answersForAgent = new Map(
                agentAnswers
                  .filter((a) => a.agentUserId === agent.id)
                  .map((a) => [a.questionId, a]),
              );
              return (
                <div
                  key={agent.id}
                  className="flex flex-col gap-2 rounded-md border border-glass-strong bg-glass p-2"
                >
                  <div className="text-sm font-medium text-foreground">
                    {agent.name}
                  </div>
                  <ul className="flex flex-col gap-1.5">
                    {agentQs.map((q) => {
                      const ans = answersForAgent.get(q.id);
                      return (
                        <li key={q.id} className="text-xs">
                          <span className="text-muted-foreground">
                            {q.label}:
                          </span>{" "}
                          <span className="text-foreground">
                            {renderAnswer(q, ans)}
                          </span>
                        </li>
                      );
                    })}
                  </ul>
                </div>
              );
            })}
          </div>
        </section>
      )}

      {detail.comment && (
        <section className="rounded-md border border-glass-strong bg-glass p-3">
          <header className="text-[10px] uppercase tracking-wider text-muted-foreground">
            Comment
          </header>
          <p className="mt-1 whitespace-pre-line text-sm text-foreground">
            {detail.comment}
          </p>
        </section>
      )}
    </div>
  );
}

function renderAnswer(
  question: {
    type: string;
    configJson?: string;
  },
  ans:
    | {
        valueNumeric: number | null;
        valueText: string | null;
        valueJson: unknown | null;
      }
    | undefined,
): string {
  if (!ans) return "—";
  const cfg = parseConfig(question.configJson);
  switch (question.type) {
    case "Rating":
    case "Nps":
      if (ans.valueNumeric === null) return "—";
      if (question.type === "Rating") {
        const labels = (cfg as RatingConfig)?.labels;
        if (Array.isArray(labels)) {
          const idx = Math.round(ans.valueNumeric) - 1;
          if (idx >= 0 && idx < labels.length) {
            return `${ans.valueNumeric} (${labels[idx]})`;
          }
        }
      }
      return String(ans.valueNumeric);
    case "Text":
      return ans.valueText ?? "—";
    case "SingleChoice": {
      if (!ans.valueText) return "—";
      const opts = (cfg as ChoiceConfig)?.options;
      const match = Array.isArray(opts)
        ? opts.find((o) => o.value === ans.valueText)
        : undefined;
      return match?.label ?? ans.valueText;
    }
    case "MultiChoice": {
      const vals = Array.isArray(ans.valueJson)
        ? (ans.valueJson as string[])
        : [];
      const opts = (cfg as ChoiceConfig)?.options;
      const optsByValue = Array.isArray(opts)
        ? new Map(opts.map((o) => [o.value, o.label]))
        : new Map<string, string>();
      const rendered = vals.map((v) => optsByValue.get(v) ?? v);
      return rendered.length > 0 ? rendered.join(", ") : "—";
    }
    default:
      return ans.valueText ?? String(ans.valueNumeric ?? "—");
  }
}

function parseConfig(configJson: string | undefined): unknown {
  if (!configJson) return {};
  try {
    return JSON.parse(configJson);
  } catch {
    return {};
  }
}
