import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  AlertCircle,
  CheckCircle2,
  Clock,
  Loader2,
  Send,
  Star,
} from "lucide-react";
import {
  publicSurveysApi,
  type ChoiceConfig,
  type PublicSurveySubmitInput,
  type RatingConfig,
  type SurveyPublicView,
  type SurveyQuestion,
} from "@/lib/surveys-api";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

type AnswerMap = Record<number, unknown>;
// Per-agent answers keyed by `${agentUserId}|${questionId}` so each
// agent's full sub-question set lives in a single map without collision.
type AgentAnswerMap = Record<string, unknown>;

function agentAnswerKey(agentUserId: string, questionId: number) {
  return `${agentUserId}|${questionId}`;
}

type SubmitState =
  | { kind: "idle" }
  | { kind: "sending" }
  | { kind: "done"; surveyName: string; message: string }
  | { kind: "error"; status: number; message: string };

/// Bare-layout page rendered at `/surveys/:token`. Customer has no session;
/// the server-side token validates every request. Same purple/blue glass
/// shell as the intake page so the brand identity carries over.
export function PublicSurveyPage({ token }: { token: string }) {
  const viewQ = useQuery({
    queryKey: ["public-survey", token],
    queryFn: () => publicSurveysApi.get(token),
    retry: false,
  });

  if (viewQ.isLoading) {
    return (
      <PublicShell>
        <div className="flex items-center gap-3 text-muted-foreground">
          <Loader2 className="h-5 w-5 animate-spin" />
          <span>Loading…</span>
        </div>
      </PublicShell>
    );
  }

  if (viewQ.isError) {
    const err = viewQ.error as Error & {
      status?: number;
      payload?: { message?: string };
    };
    // Server returns the admin-supplied message body on expired/submitted,
    // so we render that instead of an English fallback. 404 is the only
    // case where we have no message (the token didn't match anything) —
    // that triggers a separate query inside NotFoundState which hits the
    // server again to fetch the survey's notFoundMessage, but since the
    // token is unknown there's no survey to attach to. We render a
    // neutral icon-only state with no copy in that case.
    if (err.status === 410) return <ExpiredState message={err.payload?.message} />;
    if (err.status === 409) return <AlreadySubmittedState message={err.payload?.message} />;
    if (err.status === 404) return <NotFoundState />;
    return (
      <PublicShell>
        <div className="flex flex-col items-start gap-2">
          <AlertCircle className="h-6 w-6 text-red-300" />
        </div>
      </PublicShell>
    );
  }

  return <PublicSurveyForm token={token} view={viewQ.data!} />;
}

function PublicSurveyForm({
  token,
  view,
}: {
  token: string;
  view: SurveyPublicView;
}) {
  const [answers, setAnswers] = useState<AnswerMap>({});
  const [agentAnswers, setAgentAnswers] = useState<AgentAnswerMap>({});
  const [comment, setComment] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [state, setState] = useState<SubmitState>({ kind: "idle" });

  const hasAgentQuestions =
    view.agentQuestions.length > 0 && view.attributedAgents.length > 0;

  function validate(): Record<string, string> {
    const errs: Record<string, string> = {};
    for (const q of view.questions) {
      if (!q.isRequired) continue;
      const v = answers[q.id];
      if (isEmptyAnswer(v)) {
        errs[`q:${q.id}`] = "Required";
      }
    }
    if (hasAgentQuestions) {
      for (const a of view.attributedAgents) {
        for (const q of view.agentQuestions) {
          if (!q.isRequired) continue;
          const v = agentAnswers[agentAnswerKey(a.userId, q.id)];
          if (isEmptyAnswer(v)) {
            errs[`agent:${a.userId}:${q.id}`] = "Required";
          }
        }
      }
    }
    return errs;
  }

  async function submit() {
    const errs = validate();
    setErrors(errs);
    if (Object.keys(errs).length > 0) return;

    const payload: PublicSurveySubmitInput = {
      comment: comment.trim() || null,
      answers: view.questions
        .filter((q) => !isEmptyAnswer(answers[q.id]))
        .map((q) => ({ questionId: q.id, value: answers[q.id] })),
      agentAnswers: hasAgentQuestions
        ? view.attributedAgents.flatMap((agent) =>
            view.agentQuestions
              .map((q) => {
                const v = agentAnswers[agentAnswerKey(agent.userId, q.id)];
                if (isEmptyAnswer(v)) return null;
                return {
                  agentUserId: agent.userId,
                  questionId: q.id,
                  value: v,
                };
              })
              .filter((x): x is NonNullable<typeof x> => x !== null),
          )
        : [],
    };

    setState({ kind: "sending" });
    try {
      const res = await publicSurveysApi.submit(token, payload);
      setState({
        kind: "done",
        surveyName: res.surveyName,
        message: res.message,
      });
    } catch (err) {
      const e = err as Error & { status?: number; payload?: { error?: string } };
      setState({
        kind: "error",
        status: e.status ?? 0,
        message: e.payload?.error ?? "",
      });
    }
  }

  if (state.kind === "done") {
    return (
      <PublicShell>
        <header className="flex items-center gap-3">
          <CheckCircle2 className="h-8 w-8 text-emerald-400" />
          <div>
            <h1 className="text-xl font-semibold text-foreground">
              {state.surveyName}
            </h1>
            {state.message && (
              <p className="text-sm text-muted-foreground whitespace-pre-line">
                {state.message}
              </p>
            )}
          </div>
        </header>
      </PublicShell>
    );
  }

  return (
    <PublicShell>
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold text-foreground">
          {view.surveyName}
        </h1>
        {view.introHtml && (
          <div
            className="prose dark:prose-invert prose-sm max-w-none text-muted-foreground"
            // Server-sanitised admin-authored HTML. KbHtmlSanitizer ran on the
            // way in; we trust the round-trip rather than re-sanitising here.
            dangerouslySetInnerHTML={{ __html: view.introHtml }}
          />
        )}
        <p className="flex items-center gap-1 text-xs text-muted-foreground/70">
          <Clock className="h-3 w-3" />
          {new Date(view.expiresUtc).toLocaleString()}
        </p>
      </header>

      <form
        className="mt-6 flex flex-col gap-6"
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        {view.questions.map((q) => (
          <QuestionField
            key={q.id}
            question={q}
            value={answers[q.id]}
            error={errors[`q:${q.id}`]}
            onChange={(v) => setAnswers((prev) => ({ ...prev, [q.id]: v }))}
          />
        ))}

        {hasAgentQuestions && (
          <section className="flex flex-col gap-4">
            {view.agentBlockHeading && (
              <div className="border-b border-glass-strong pb-1">
                <h3 className="text-sm font-semibold uppercase tracking-wider text-muted-foreground/80">
                  {view.agentBlockHeading}
                </h3>
              </div>
            )}
            {view.attributedAgents.map((agent) => (
              <article
                key={agent.userId}
                className="flex flex-col gap-3 rounded-lg border border-glass-strong bg-glass p-4"
              >
                <header className="text-sm font-medium text-foreground">
                  {agent.displayName}
                </header>
                {view.agentQuestions.map((q) => (
                  <QuestionField
                    key={`${agent.userId}:${q.id}`}
                    question={q}
                    value={agentAnswers[agentAnswerKey(agent.userId, q.id)]}
                    error={errors[`agent:${agent.userId}:${q.id}`]}
                    onChange={(v) =>
                      setAgentAnswers((prev) => ({
                        ...prev,
                        [agentAnswerKey(agent.userId, q.id)]: v,
                      }))
                    }
                  />
                ))}
              </article>
            ))}
          </section>
        )}

        {state.kind === "error" && state.message && (
          <div className="rounded-md border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200">
            {state.message}
          </div>
        )}

        <footer className="flex justify-end">
          <Button
            type="submit"
            disabled={state.kind === "sending" || !view.submitButtonLabel}
            className="min-w-[140px]"
          >
            {state.kind === "sending" ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Send className="h-4 w-4" />
            )}
            {view.submitButtonLabel}
          </Button>
        </footer>
        {/* Optional admin-side comment field is no longer hardcoded —
            admins add a free-text Survey question via the designer when
            they want one. */}
        <input
          type="hidden"
          value={comment}
          onChange={(e) => setComment(e.target.value)}
        />
      </form>
    </PublicShell>
  );
}

function isEmptyAnswer(v: unknown): boolean {
  if (v === undefined || v === null) return true;
  if (typeof v === "string" && v.trim().length === 0) return true;
  if (Array.isArray(v) && v.length === 0) return true;
  return false;
}

// ============================================================
// Building blocks
// ============================================================

function ScalePicker({
  points,
  value,
  onChange,
  labels,
}: {
  points: number;
  value: number | null;
  onChange: (v: number) => void;
  labels?: string[];
}) {
  const items = useMemo(
    () => Array.from({ length: points }, (_, i) => i + 1),
    [points],
  );
  return (
    <div>
      <div className="flex flex-wrap gap-2">
        {items.map((n) => {
          const active = value !== null && n <= value;
          return (
            <button
              key={n}
              type="button"
              onClick={() => onChange(n)}
              aria-pressed={value === n}
              className={cn(
                "flex h-9 min-w-[36px] items-center justify-center gap-1 rounded-md border px-2 text-sm transition",
                active
                  ? "border-primary/50 bg-primary/15 text-primary-foreground"
                  : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
              )}
            >
              <Star className={cn("h-4 w-4", active ? "fill-current" : "")} />
              {labels?.[n - 1] ? (
                <span className="text-xs">{labels[n - 1]}</span>
              ) : (
                <span className="text-xs font-medium">{n}</span>
              )}
            </button>
          );
        })}
      </div>
    </div>
  );
}

function QuestionField({
  question,
  value,
  error,
  onChange,
}: {
  question: SurveyQuestion;
  value: unknown;
  error: string | undefined;
  onChange: (next: unknown) => void;
}) {
  const labelEl = (
    <span className="text-sm font-medium text-foreground">
      {question.label}
      {question.isRequired && <span className="ml-1 text-red-400">*</span>}
    </span>
  );
  const helpEl = question.helpText ? (
    <span className="text-xs text-muted-foreground">{question.helpText}</span>
  ) : null;

  switch (question.type) {
    case "Rating": {
      const cfg = (question.config ?? {}) as RatingConfig;
      const pts = Number(cfg.points) > 0 ? Number(cfg.points) : 5;
      return (
        <div className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <ScalePicker
            points={pts}
            value={typeof value === "number" ? value : null}
            onChange={(v) => onChange(v)}
            labels={Array.isArray(cfg.labels) ? cfg.labels : undefined}
          />
          {error && <span className="text-xs text-red-300">{error}</span>}
        </div>
      );
    }
    case "Nps": {
      return (
        <div className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <div className="flex flex-wrap gap-1.5">
            {Array.from({ length: 11 }, (_, i) => i).map((n) => {
              const selected = value === n;
              const tint =
                n <= 6
                  ? "border-red-400/30 hover:bg-red-500/10"
                  : n <= 8
                    ? "border-amber-400/30 hover:bg-amber-500/10"
                    : "border-emerald-400/30 hover:bg-emerald-500/10";
              return (
                <button
                  key={n}
                  type="button"
                  onClick={() => onChange(n)}
                  aria-pressed={selected}
                  className={cn(
                    "h-9 w-9 rounded-md border text-sm transition",
                    selected
                      ? "border-primary/60 bg-primary/20 text-primary-foreground"
                      : `${tint} bg-glass text-muted-foreground`,
                  )}
                >
                  {n}
                </button>
              );
            })}
          </div>
          {error && <span className="text-xs text-red-300">{error}</span>}
        </div>
      );
    }
    case "Text": {
      return (
        <label className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <textarea
            value={typeof value === "string" ? value : ""}
            onChange={(e) => onChange(e.target.value)}
            maxLength={4000}
            rows={4}
            className={cn(
              "resize-y rounded-md border bg-glass px-3 py-2 text-sm text-foreground outline-none focus:border-primary/40",
              error ? "border-red-400/50" : "border-glass-strong",
            )}
          />
          {error && <span className="text-xs text-red-300">{error}</span>}
        </label>
      );
    }
    case "SingleChoice": {
      const cfg = (question.config ?? {}) as ChoiceConfig;
      const opts = Array.isArray(cfg.options) ? cfg.options : [];
      return (
        <div className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <div className="flex flex-col gap-1.5">
            {opts.map((o) => {
              const selected = value === o.value;
              return (
                <label
                  key={o.value}
                  className={cn(
                    "flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm transition",
                    selected
                      ? "border-primary/50 bg-primary/15 text-primary-foreground"
                      : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                  )}
                >
                  <input
                    type="radio"
                    checked={selected}
                    onChange={() => onChange(o.value)}
                    className="h-4 w-4"
                  />
                  {o.label}
                </label>
              );
            })}
          </div>
          {error && <span className="text-xs text-red-300">{error}</span>}
        </div>
      );
    }
    case "MultiChoice": {
      const cfg = (question.config ?? {}) as ChoiceConfig;
      const opts = Array.isArray(cfg.options) ? cfg.options : [];
      const selected = Array.isArray(value) ? (value as string[]) : [];
      return (
        <div className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <div className="flex flex-col gap-1.5">
            {opts.map((o) => {
              const checked = selected.includes(o.value);
              return (
                <label
                  key={o.value}
                  className={cn(
                    "flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm transition",
                    checked
                      ? "border-primary/50 bg-primary/15 text-primary-foreground"
                      : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                  )}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={(e) => {
                      const next = e.target.checked
                        ? [...selected, o.value]
                        : selected.filter((v) => v !== o.value);
                      onChange(next);
                    }}
                    className="h-4 w-4"
                  />
                  {o.label}
                </label>
              );
            })}
          </div>
          {error && <span className="text-xs text-red-300">{error}</span>}
        </div>
      );
    }
    default:
      return null;
  }
}

// ============================================================
// Shell + state pages
// ============================================================

function PublicShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="app-background min-h-screen w-full px-4 py-12">
      <div className="mx-auto max-w-xl rounded-2xl border border-glass-strong bg-glass p-8 shadow-2xl backdrop-blur-xl">
        {children}
      </div>
    </div>
  );
}

function ExpiredState({ message }: { message?: string }) {
  return (
    <PublicShell>
      <div className="flex flex-col items-start gap-3">
        <Clock className="h-6 w-6 text-orange-300" />
        {message && (
          <p className="text-sm text-muted-foreground whitespace-pre-line">
            {message}
          </p>
        )}
      </div>
    </PublicShell>
  );
}

function NotFoundState() {
  // 404 means the token didn't match anything, so we can't look up the
  // survey-specific not-found message. Render an icon-only state so we
  // don't leak whether tokens ever existed (and so we don't slip an
  // English string past a Dutch tenant).
  return (
    <PublicShell>
      <div className="flex flex-col items-start gap-3">
        <AlertCircle className="h-6 w-6 text-muted-foreground" />
      </div>
    </PublicShell>
  );
}

function AlreadySubmittedState({ message }: { message?: string }) {
  return (
    <PublicShell>
      <div className="flex flex-col items-start gap-3">
        <CheckCircle2 className="h-6 w-6 text-emerald-300" />
        {message && (
          <p className="text-sm text-muted-foreground whitespace-pre-line">
            {message}
          </p>
        )}
      </div>
    </PublicShell>
  );
}
