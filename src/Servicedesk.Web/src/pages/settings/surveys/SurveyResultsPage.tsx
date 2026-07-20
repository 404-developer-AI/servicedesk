import { useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import {
  ArrowLeft,
  BarChart3,
  Loader2,
  MailQuestion,
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
import { cn } from "@/lib/utils";
import {
  surveyResultsApi,
  surveysApi,
  type RatingConfig,
  type ChoiceConfig,
  type SurveyResponseDetail,
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
                      {new Date(inv.sentUtc).toLocaleString()}
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
          className="rounded-lg border border-glass-strong bg-gradient-to-br from-white/[0.03] to-white/[0.01] p-4"
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
          {new Date(detail.sentUtc).toLocaleString()} · submitted{" "}
          {new Date(detail.submittedUtc).toLocaleString()}
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
