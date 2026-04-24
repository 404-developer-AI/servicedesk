import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertCircle, CheckCircle2, Clock, Loader2, Send } from "lucide-react";
import {
  publicIntakeApi,
  type IntakePublicView,
} from "@/lib/intakeForms-api";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";

type AnswerMap = Record<string, unknown>;

type SubmitState =
  | { kind: "idle" }
  | { kind: "sending" }
  | {
      kind: "done";
      templateName: string;
      answers: Array<{ questionId: number; answer: unknown }>;
    }
  | { kind: "error"; status: number; message: string };

/// Bare-layout page shown at `/intake/:token`. The customer has no session;
/// the server-side token validates every request. We render a glass-card
/// centered on the purple/blue mesh background used elsewhere to stay on
/// brand even though the app shell is hidden.
export function PublicIntakeFormPage({ token }: { token: string }) {
  const viewQ = useQuery({
    queryKey: ["public-intake", token],
    queryFn: () => publicIntakeApi.get(token),
    retry: false,
  });

  if (viewQ.isLoading) {
    return (
      <PublicShell>
        <div className="flex items-center gap-3 text-muted-foreground">
          <Loader2 className="h-5 w-5 animate-spin" />
          <span>Formulier laden…</span>
        </div>
      </PublicShell>
    );
  }

  if (viewQ.isError) {
    const err = viewQ.error as Error & { status?: number };
    if (err.status === 410) return <ExpiredState />;
    if (err.status === 404) return <NotFoundState />;
    if (err.status === 409) return <AlreadySubmittedState />;
    return (
      <PublicShell>
        <div className="flex flex-col items-start gap-2">
          <div className="flex items-center gap-2 text-red-300">
            <AlertCircle className="h-5 w-5" />
            <span className="text-sm font-medium">Er ging iets mis</span>
          </div>
          <p className="text-xs text-muted-foreground">
            Kon het formulier niet laden. Probeer de link opnieuw te openen.
          </p>
        </div>
      </PublicShell>
    );
  }

  return <PublicIntakeContent token={token} view={viewQ.data!} />;
}

function PublicIntakeContent({
  token,
  view,
}: {
  token: string;
  view: IntakePublicView;
}) {
  const initialAnswers = useMemo(() => {
    const map: AnswerMap = {};
    for (const q of view.questions) {
      if (q.type === "SectionHeader") continue;
      const key = q.id.toString();
      if (view.prefill && key in view.prefill) {
        map[key] = (view.prefill as Record<string, unknown>)[key];
      }
    }
    return map;
  }, [view]);

  const [answers, setAnswers] = useState<AnswerMap>(initialAnswers);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [state, setState] = useState<SubmitState>({ kind: "idle" });

  function validate(): Record<string, string> {
    const errs: Record<string, string> = {};
    for (const q of view.questions) {
      if (q.type === "SectionHeader") continue;
      const key = q.id.toString();
      const v = answers[key];
      if (q.isRequired) {
        if (v === undefined || v === null) {
          errs[key] = "Verplicht";
          continue;
        }
        if (typeof v === "string" && v.trim().length === 0) {
          errs[key] = "Verplicht";
          continue;
        }
        if (Array.isArray(v) && v.length === 0) {
          errs[key] = "Verplicht";
          continue;
        }
      }
    }
    return errs;
  }

  async function submit() {
    const errs = validate();
    setErrors(errs);
    if (Object.keys(errs).length > 0) return;

    const payload: AnswerMap = {};
    for (const q of view.questions) {
      if (q.type === "SectionHeader") continue;
      const key = q.id.toString();
      const v = answers[key];
      if (v === undefined) continue;
      if (typeof v === "string" && v.trim().length === 0) continue;
      payload[key] = v;
    }

    setState({ kind: "sending" });
    try {
      const res = await publicIntakeApi.submit(token, payload);
      setState({
        kind: "done",
        templateName: res.templateName,
        answers: res.answers,
      });
    } catch (err) {
      const e = err as Error & { status?: number; payload?: { error?: string } };
      setState({
        kind: "error",
        status: e.status ?? 0,
        message: e.payload?.error ?? "Kon het formulier niet indienen.",
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
              {state.templateName}
            </h1>
            <p className="text-sm text-muted-foreground">
              Bedankt — je antwoorden zijn ontvangen.
            </p>
          </div>
        </header>

        <section className="mt-6 rounded-lg border border-white/[0.06] bg-white/[0.02] p-5">
          <h2 className="mb-3 text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Overzicht
          </h2>
          <ReadOnlySummary view={view} answers={state.answers} />
        </section>
      </PublicShell>
    );
  }

  return (
    <PublicShell>
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold text-foreground">
          {view.templateName}
        </h1>
        {view.templateDescription && (
          <p className="text-sm text-muted-foreground">
            {view.templateDescription}
          </p>
        )}
        {view.expiresUtc && (
          <p className="flex items-center gap-1 text-xs text-muted-foreground/70">
            <Clock className="h-3 w-3" />
            Geldig tot {new Date(view.expiresUtc).toLocaleString()}
          </p>
        )}
      </header>

      <form
        className="mt-6 flex flex-col gap-5"
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        {view.questions.map((q) => (
          <QuestionField
            key={q.id}
            question={q}
            value={answers[q.id.toString()]}
            error={errors[q.id.toString()]}
            onChange={(v) =>
              setAnswers((prev) => ({ ...prev, [q.id.toString()]: v }))
            }
          />
        ))}

        {state.kind === "error" && (
          <div className="rounded-md border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200">
            {state.message}
          </div>
        )}

        <footer className="flex justify-end">
          <Button
            type="submit"
            disabled={state.kind === "sending"}
            className="min-w-[140px]"
          >
            {state.kind === "sending" ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Send className="h-4 w-4" />
            )}
            {state.kind === "sending" ? "Versturen…" : "Formulier indienen"}
          </Button>
        </footer>
      </form>
    </PublicShell>
  );
}

function QuestionField({
  question,
  value,
  error,
  onChange,
}: {
  question: IntakePublicView["questions"][number];
  value: unknown;
  error: string | undefined;
  onChange: (next: unknown) => void;
}) {
  if (question.type === "SectionHeader") {
    return (
      <div className="border-b border-white/[0.06] pb-1">
        <h3 className="text-sm font-semibold uppercase tracking-wider text-muted-foreground/80">
          {question.label}
        </h3>
        {question.helpText && (
          <p className="mt-1 text-xs text-muted-foreground">{question.helpText}</p>
        )}
      </div>
    );
  }

  const labelEl = (
    <span className="text-sm font-medium text-foreground">
      {question.label}
      {question.isRequired && <span className="ml-1 text-red-400">*</span>}
    </span>
  );
  const helpEl = question.helpText ? (
    <span className="text-xs text-muted-foreground">{question.helpText}</span>
  ) : null;

  const baseInputCn = cn(
    "rounded-md border bg-white/[0.02] px-3 text-sm text-foreground outline-none focus:border-primary/40",
    error ? "border-red-400/50" : "border-white/[0.08]",
  );

  switch (question.type) {
    case "ShortText":
      return (
        <label className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <input
            type="text"
            value={typeof value === "string" ? value : ""}
            onChange={(e) => onChange(e.target.value)}
            maxLength={500}
            className={cn(baseInputCn, "h-9")}
          />
          {error && <span className="text-xs text-red-300">{error}</span>}
        </label>
      );
    case "LongText":
      return (
        <label className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <textarea
            value={typeof value === "string" ? value : ""}
            onChange={(e) => onChange(e.target.value)}
            maxLength={10000}
            rows={5}
            className={cn(baseInputCn, "resize-y py-2")}
          />
          {error && <span className="text-xs text-red-300">{error}</span>}
        </label>
      );
    case "Number":
      return (
        <label className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <input
            type="number"
            value={typeof value === "number" ? value : typeof value === "string" ? value : ""}
            onChange={(e) => {
              const raw = e.target.value;
              if (raw === "") {
                onChange(null);
                return;
              }
              const parsed = Number(raw);
              onChange(Number.isFinite(parsed) ? parsed : raw);
            }}
            className={cn(baseInputCn, "h-9")}
          />
          {error && <span className="text-xs text-red-300">{error}</span>}
        </label>
      );
    case "Date":
      return (
        <label className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <input
            type="date"
            value={typeof value === "string" ? value.slice(0, 10) : ""}
            onChange={(e) => onChange(e.target.value || null)}
            className={cn(baseInputCn, "h-9")}
          />
          {error && <span className="text-xs text-red-300">{error}</span>}
        </label>
      );
    case "YesNo":
      return (
        <div className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <div className="flex gap-4">
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <input
                type="radio"
                checked={value === true}
                onChange={() => onChange(true)}
                className="h-4 w-4"
              />
              Ja
            </label>
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <input
                type="radio"
                checked={value === false}
                onChange={() => onChange(false)}
                className="h-4 w-4"
              />
              Nee
            </label>
          </div>
          {error && <span className="text-xs text-red-300">{error}</span>}
        </div>
      );
    case "DropdownSingle": {
      const current = typeof value === "string" && value.length > 0 ? value : undefined;
      return (
        <div className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <Select
            value={current}
            onValueChange={(v) => onChange(v || null)}
          >
            <SelectTrigger
              className={cn(
                "h-9 bg-white/[0.02] focus:ring-0",
                error ? "border-red-400/50" : "border-white/[0.08] focus:border-primary/40",
              )}
            >
              <SelectValue placeholder="— kies een optie —" />
            </SelectTrigger>
            <SelectContent className="border-white/10 bg-popover/80 backdrop-blur-xl">
              {question.options.map((o) => (
                <SelectItem key={o.value} value={o.value}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {error && <span className="text-xs text-red-300">{error}</span>}
        </div>
      );
    }
    case "DropdownMulti": {
      const selected = Array.isArray(value) ? (value as string[]) : [];
      return (
        <div className="flex flex-col gap-1.5">
          {labelEl}
          {helpEl}
          <div className="flex flex-col gap-1 rounded-md border border-white/[0.08] bg-white/[0.02] p-2">
            {question.options.map((o) => {
              const checked = selected.includes(o.value);
              return (
                <label
                  key={o.value}
                  className="flex items-center gap-2 text-sm text-muted-foreground"
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

function ReadOnlySummary({
  view,
  answers,
}: {
  view: IntakePublicView;
  answers: Array<{ questionId: number; answer: unknown }>;
}) {
  const byQuestionId = new Map(answers.map((a) => [a.questionId, a.answer]));
  return (
    <dl className="flex flex-col gap-3">
      {view.questions
        .filter((q) => q.type !== "SectionHeader")
        .map((q) => {
          const raw = byQuestionId.get(q.id);
          return (
            <div key={q.id} className="flex flex-col gap-0.5">
              <dt className="text-xs text-muted-foreground/80">{q.label}</dt>
              <dd className="text-sm text-foreground">{formatAnswer(q, raw)}</dd>
            </div>
          );
        })}
    </dl>
  );
}

function formatAnswer(
  q: IntakePublicView["questions"][number],
  raw: unknown,
): string {
  if (raw === undefined || raw === null || raw === "") return "—";
  switch (q.type) {
    case "YesNo":
      return raw === true ? "Ja" : "Nee";
    case "DropdownSingle": {
      const opt = q.options.find((o) => o.value === raw);
      return opt?.label ?? String(raw);
    }
    case "DropdownMulti": {
      if (!Array.isArray(raw)) return String(raw);
      const labels = raw.map(
        (v) => q.options.find((o) => o.value === v)?.label ?? String(v),
      );
      return labels.join(", ");
    }
    default:
      return String(raw);
  }
}

function PublicShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen w-full bg-gradient-to-br from-[#0b0420] via-[#100636] to-[#050817] px-4 py-12">
      <div className="mx-auto max-w-xl rounded-2xl border border-white/[0.06] bg-white/[0.03] p-8 shadow-2xl backdrop-blur-xl">
        {children}
      </div>
    </div>
  );
}

function ExpiredState() {
  return (
    <PublicShell>
      <div className="flex flex-col items-start gap-3">
        <div className="flex items-center gap-2 text-orange-300">
          <Clock className="h-6 w-6" />
          <h1 className="text-lg font-semibold">Formulier verlopen</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          De link naar dit formulier is niet meer geldig. Vraag je contactpersoon
          om een nieuwe te sturen.
        </p>
      </div>
    </PublicShell>
  );
}

function NotFoundState() {
  return (
    <PublicShell>
      <div className="flex flex-col items-start gap-3">
        <div className="flex items-center gap-2 text-muted-foreground">
          <AlertCircle className="h-6 w-6" />
          <h1 className="text-lg font-semibold">Formulier niet gevonden</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          De link is ongeldig of bestaat niet meer. Controleer of je de volledige
          URL hebt geopend.
        </p>
      </div>
    </PublicShell>
  );
}

function AlreadySubmittedState() {
  return (
    <PublicShell>
      <div className="flex flex-col items-start gap-3">
        <div className="flex items-center gap-2 text-emerald-300">
          <CheckCircle2 className="h-6 w-6" />
          <h1 className="text-lg font-semibold">Formulier al ingediend</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          De antwoorden op dit formulier zijn al verstuurd. Heb je een correctie
          nodig? Neem dan contact op met je helpdesk.
        </p>
      </div>
    </PublicShell>
  );
}
