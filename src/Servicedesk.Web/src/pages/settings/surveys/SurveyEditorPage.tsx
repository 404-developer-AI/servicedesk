import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  ChevronDown,
  Link as LinkIcon,
  Loader2,
  Plus,
  Save,
  Tag,
  Trash2,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { RichTextEditor } from "@/components/RichTextEditor";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  composeTemplatesApi,
  type ComposeTokenInfo,
} from "@/lib/composeTemplates-api";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import {
  surveysApi,
  type ChoiceConfig,
  type RatingConfig,
  type Survey,
  type SurveyQuestionScope,
  type SurveyQuestionType,
  type SurveyUpsertInput,
} from "@/lib/surveys-api";

interface DraftQuestion {
  clientId: string;
  type: SurveyQuestionType;
  label: string;
  helpText: string;
  isRequired: boolean;
  // Rating
  ratingPoints: number;
  ratingLabels: string;
  // Choice
  choiceOptions: Array<{ value: string; label: string }>;
}

function emptyDraft(): DraftQuestion {
  return {
    clientId: crypto.randomUUID(),
    type: "Rating",
    label: "",
    helpText: "",
    isRequired: false,
    ratingPoints: 5,
    ratingLabels: "",
    choiceOptions: [{ value: "yes", label: "Yes" }, { value: "no", label: "No" }],
  };
}

function fromServerQuestion(q: Survey["questions"][number]): DraftQuestion {
  const cfg = q.config as Record<string, unknown> | null;
  const rating = (cfg ?? {}) as unknown as RatingConfig;
  const choice = (cfg ?? {}) as unknown as ChoiceConfig;
  return {
    clientId: crypto.randomUUID(),
    type: q.type,
    label: q.label,
    helpText: q.helpText ?? "",
    isRequired: q.isRequired,
    ratingPoints: typeof rating.points === "number" ? rating.points : 5,
    ratingLabels: Array.isArray(rating.labels) ? rating.labels.join(" | ") : "",
    choiceOptions: Array.isArray(choice.options)
      ? choice.options.map((o) => ({ value: o.value, label: o.label }))
      : [],
  };
}

function toApiQuestion(
  q: DraftQuestion,
  sortOrder: number,
  appliesTo: SurveyQuestionScope,
) {
  let config: unknown = {};
  if (q.type === "Rating") {
    const labels = q.ratingLabels
      .split("|")
      .map((s) => s.trim())
      .filter(Boolean);
    config = {
      points: Math.max(2, Math.min(10, q.ratingPoints || 5)),
      ...(labels.length === q.ratingPoints ? { labels } : {}),
    };
  } else if (q.type === "SingleChoice" || q.type === "MultiChoice") {
    config = {
      options: q.choiceOptions
        .map((o) => ({ value: o.value.trim(), label: o.label.trim() }))
        .filter((o) => o.value && o.label),
    };
  }
  return {
    sortOrder,
    type: q.type,
    appliesTo,
    label: q.label.trim(),
    helpText: q.helpText.trim() || null,
    isRequired: q.isRequired,
    config,
  };
}

// Survey-specific placeholders resolved at dispatch time on top of the
// standard compose tokens. Mirrors SurveyTokens on the backend.
const SURVEY_INVITE_TOKENS: ComposeTokenInfo[] = [
  { token: "{{survey.link}}", label: "Survey · Link" },
  { token: "{{ticket.agentNames}}", label: "Ticket · Agent names" },
];

// Minimal structural type for the Tiptap editor handle we need from
// RichTextEditor — just enough to drop a placeholder / link at the caret.
type EditorHandle = {
  chain: () => {
    focus: () => {
      insertContent: (content: string) => { run: () => void };
    };
  };
};

/// Dropdown that lists the available `{{token}}` placeholders. Mirrors the
/// compose-template editor's "Insert variable" picker so the two surfaces
/// feel identical. `onPick` receives the literal token string.
function VariableMenu({
  tokens,
  onPick,
}: {
  tokens: ComposeTokenInfo[];
  onPick: (token: string) => void;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className={cn(
            "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
            "border-violet-400/30 bg-violet-400/10 text-violet-200 hover:bg-violet-400/15",
          )}
          title="Insert a placeholder that resolves to the recipient's ticket data at send time"
        >
          <Tag className="h-3.5 w-3.5" />
          Insert variable
          <ChevronDown className="h-3.5 w-3.5" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="max-h-80 w-72 overflow-auto">
        <DropdownMenuLabel className="text-[11px] uppercase tracking-wider text-muted-foreground/70">
          Available variables
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {tokens.length === 0 ? (
          <div className="px-2 py-2 text-xs text-muted-foreground">Loading…</div>
        ) : (
          tokens.map((t) => (
            <DropdownMenuItem
              key={t.token}
              onSelect={(e) => {
                e.preventDefault();
                onPick(t.token);
              }}
              className="flex items-center justify-between gap-3"
            >
              <span className="truncate text-sm">{t.label}</span>
              <code className="shrink-0 rounded bg-glass-strong px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                {t.token}
              </code>
            </DropdownMenuItem>
          ))
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export function SurveyEditorPage({ surveyId }: { surveyId: string | null }) {
  const navigate = useNavigate();
  const qc = useQueryClient();

  const existingQ = useQuery({
    queryKey: ["surveys", "detail", surveyId],
    queryFn: () => surveysApi.get(surveyId!),
    enabled: surveyId !== null,
  });

  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [introHtml, setIntroHtml] = useState("");
  const [inviteSubject, setInviteSubject] = useState("");
  const [inviteBodyHtml, setInviteBodyHtml] = useState("");
  const [isActive, setIsActive] = useState(true);
  const [ttlDays, setTtlDays] = useState<string>("");

  // Five admin-supplied text fields the public page renders. We never
  // back-fill with English defaults so a Dutch admin can leave the survey
  // entirely in Dutch (the server requires the four non-heading fields to
  // be non-empty on save).
  const [agentBlockHeading, setAgentBlockHeading] = useState("");
  const [submitButtonLabel, setSubmitButtonLabel] = useState("");
  const [thankYouMessage, setThankYouMessage] = useState("");
  const [expiredMessage, setExpiredMessage] = useState("");
  const [notFoundMessage, setNotFoundMessage] = useState("");

  // Two separate question lists: Survey-scope (asked once) and Agent-scope
  // (rendered once per attributed agent at submit time).
  const [surveyQuestions, setSurveyQuestions] = useState<DraftQuestion[]>([]);
  const [agentQuestions, setAgentQuestions] = useState<DraftQuestion[]>([]);

  // Token picker metadata — the standard compose tokens plus the two
  // survey-specific placeholders. Shared admin-only endpoint, cached hard.
  const tokensQ = useQuery({
    queryKey: ["compose-templates", "tokens"],
    queryFn: () => composeTemplatesApi.listTokens(),
    staleTime: Infinity,
  });
  const inviteTokens = useMemo(
    () => [...(tokensQ.data?.tokens ?? []), ...SURVEY_INVITE_TOKENS],
    [tokensQ.data],
  );

  // Live editor handle so the variable-picker can drop a placeholder (or a
  // ready-made survey link) at the current caret position.
  const bodyEditorRef = useRef<EditorHandle | null>(null);

  const insertIntoBody = (content: string) => {
    bodyEditorRef.current?.chain().focus().insertContent(content).run();
  };

  useEffect(() => {
    if (!existingQ.data) return;
    const s = existingQ.data;
    setName(s.name);
    setDescription(s.description ?? "");
    setIntroHtml(s.introHtml);
    setInviteSubject(s.inviteSubject);
    setInviteBodyHtml(s.inviteBodyHtml);
    setIsActive(s.isActive);
    setTtlDays(s.ttlDays ? String(s.ttlDays) : "");
    setAgentBlockHeading(s.agentBlockHeading ?? "");
    setSubmitButtonLabel(s.submitButtonLabel);
    setThankYouMessage(s.thankYouMessage);
    setExpiredMessage(s.expiredMessage);
    setNotFoundMessage(s.notFoundMessage);

    const survey = s.questions
      .filter((q) => q.appliesTo === "Survey")
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .map(fromServerQuestion);
    const agent = s.questions
      .filter((q) => q.appliesTo === "Agent")
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .map(fromServerQuestion);
    setSurveyQuestions(survey);
    setAgentQuestions(agent);
  }, [existingQ.data]);

  const save = useMutation({
    mutationFn: async () => {
      const input: SurveyUpsertInput = {
        name: name.trim(),
        description: description.trim() || null,
        introHtml,
        inviteSubject: inviteSubject.trim(),
        inviteBodyHtml,
        isActive,
        ttlDays: ttlDays ? Math.max(1, Math.min(365, parseInt(ttlDays, 10))) : null,
        agentBlockHeading: agentBlockHeading.trim() || null,
        submitButtonLabel: submitButtonLabel.trim(),
        thankYouMessage: thankYouMessage.trim(),
        expiredMessage: expiredMessage.trim(),
        notFoundMessage: notFoundMessage.trim(),
        questions: [
          ...surveyQuestions.map((q, i) => toApiQuestion(q, i, "Survey")),
          ...agentQuestions.map((q, i) => toApiQuestion(q, i, "Agent")),
        ],
      };
      return surveyId
        ? surveysApi.update(surveyId, input)
        : surveysApi.create(input);
    },
    onSuccess: (saved) => {
      toast.success("Survey saved.");
      qc.invalidateQueries({ queryKey: ["surveys", "list"] });
      qc.invalidateQueries({ queryKey: ["surveys", "detail", saved.id] });
      qc.invalidateQueries({ queryKey: ["surveys", "usable"] });
      if (!surveyId) {
        navigate({
          to: "/settings/surveys/$surveyId",
          params: { surveyId: saved.id },
          replace: true,
        });
      }
    },
    onError: (err) => {
      const e = err as Error & { payload?: { error?: string } };
      toast.error(e.payload?.error ?? "Could not save survey.");
    },
  });

  const canSave = useMemo(
    () =>
      name.trim().length > 0 &&
      submitButtonLabel.trim().length > 0 &&
      thankYouMessage.trim().length > 0 &&
      expiredMessage.trim().length > 0 &&
      notFoundMessage.trim().length > 0 &&
      !save.isPending,
    [
      name,
      submitButtonLabel,
      thankYouMessage,
      expiredMessage,
      notFoundMessage,
      save.isPending,
    ],
  );

  if (surveyId && existingQ.isLoading) {
    return <Skeleton className="h-40 w-full" />;
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-center justify-between gap-3">
        <div className="space-y-1">
          <h1 className="text-display-md font-semibold text-foreground">
            {surveyId ? "Edit survey" : "New survey"}
          </h1>
          <p className="text-sm text-muted-foreground">
            Design the survey customers receive. Save then attach via
            <span className="font-medium"> send_survey</span> trigger or a
            compose template.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => navigate({ to: "/settings/surveys" })}
          >
            <ArrowLeft className="h-4 w-4" />
            Back to list
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={!canSave}
            size="sm"
          >
            {save.isPending ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Save className="h-4 w-4" />
            )}
            Save survey
          </Button>
        </div>
      </header>

      <section className="grid gap-4 rounded-lg border border-glass-strong bg-glass p-5 md:grid-cols-2">
        <div className="md:col-span-2">
          <Label>Name</Label>
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. Post-resolution CSAT"
            maxLength={200}
          />
        </div>
        <div className="md:col-span-2">
          <Label>Internal description (admin only)</Label>
          <Input
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Not shown to customers"
            maxLength={2000}
          />
        </div>
        <div>
          <Label>Link expires after (days)</Label>
          <Input
            type="number"
            value={ttlDays}
            onChange={(e) => setTtlDays(e.target.value)}
            placeholder="Leave blank for the system default"
            min={1}
            max={365}
          />
        </div>
        <div className="flex items-center justify-between rounded-md border border-glass-strong bg-glass px-3 py-2">
          <div>
            <Label className="mb-0">Active</Label>
            <p className="text-xs text-muted-foreground">
              Inactive surveys cannot be sent; existing pending invitations
              keep working until they expire or are cancelled.
            </p>
          </div>
          <Switch checked={isActive} onCheckedChange={setIsActive} />
        </div>
      </section>

      <section className="flex flex-col gap-3 rounded-lg border border-glass-strong bg-glass p-5">
        <header>
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Invitation email
          </h2>
          <p className="text-xs text-muted-foreground">
            Sent when the survey fires. Use{" "}
            <span className="font-medium text-foreground/80">
              Insert variable
            </span>{" "}
            to drop a placeholder like{" "}
            <code className="font-mono">{"{{contact.firstName}}"}</code> — it is
            filled in for each recipient at send time.
          </p>
        </header>
        <div>
          <div className="flex items-center justify-between">
            <Label>Subject</Label>
            <VariableMenu
              tokens={inviteTokens}
              onPick={(token) =>
                setInviteSubject((s) => (s ? `${s} ${token}` : token))
              }
            />
          </div>
          <Input
            value={inviteSubject}
            onChange={(e) => setInviteSubject(e.target.value)}
            placeholder={"How did we do on #{{ticket.number}}?"}
            maxLength={300}
          />
        </div>
        <div>
          <div className="flex items-center justify-between">
            <Label>Body</Label>
            <div className="flex items-center gap-1.5">
              <button
                type="button"
                onClick={() =>
                  insertIntoBody(
                    '<a href="{{survey.link}}">Open the survey</a>',
                  )
                }
                className={cn(
                  "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
                  "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
                )}
                title="Insert a ready-made link to the survey at the cursor"
              >
                <LinkIcon className="h-3.5 w-3.5" />
                Insert survey link
              </button>
              <VariableMenu
                tokens={inviteTokens}
                onPick={(token) => insertIntoBody(token)}
              />
            </div>
          </div>
          <RichTextEditor
            content={inviteBodyHtml}
            onChange={setInviteBodyHtml}
            placeholder="Write the invitation. Use 'Insert survey link' to add the clickable link customers click to open the survey…"
            minHeight="160px"
            maxHeight="420px"
            onEditorReady={(editor) => {
              bodyEditorRef.current = editor as unknown as EditorHandle | null;
            }}
          />
          <p className="mt-1 text-[11px] text-muted-foreground/70">
            Empty placeholders stay as the raw <code className="font-mono">{"{{...}}"}</code>{" "}
            text, so double-check the recipient's data is filled in.
          </p>
        </div>
        <div>
          <Label>Intro paragraph on the survey page (HTML)</Label>
          <textarea
            value={introHtml}
            onChange={(e) => setIntroHtml(e.target.value)}
            rows={4}
            className="w-full resize-y rounded-md border border-glass-strong bg-glass px-3 py-2 text-sm text-foreground outline-none focus:border-primary/40"
            placeholder="Optional — leave blank for no intro paragraph."
          />
        </div>
      </section>

      <section className="flex flex-col gap-3 rounded-lg border border-glass-strong bg-glass p-5">
        <header>
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Survey page labels
          </h2>
          <p className="text-xs text-muted-foreground">
            Every line of text on the public page. Fill these in your own
            language — there are no built-in defaults.
          </p>
        </header>
        <div>
          <Label>Submit button label *</Label>
          <Input
            value={submitButtonLabel}
            onChange={(e) => setSubmitButtonLabel(e.target.value)}
            placeholder="e.g. Submit  /  Versturen  /  Envoyer"
            maxLength={500}
          />
        </div>
        <div>
          <Label>Thank-you message *</Label>
          <textarea
            value={thankYouMessage}
            onChange={(e) => setThankYouMessage(e.target.value)}
            rows={3}
            className="w-full resize-y rounded-md border border-glass-strong bg-glass px-3 py-2 text-sm text-foreground outline-none focus:border-primary/40"
            placeholder="Shown after the customer submits."
            maxLength={5000}
          />
        </div>
        <div>
          <Label>Expired link message *</Label>
          <textarea
            value={expiredMessage}
            onChange={(e) => setExpiredMessage(e.target.value)}
            rows={3}
            className="w-full resize-y rounded-md border border-glass-strong bg-glass px-3 py-2 text-sm text-foreground outline-none focus:border-primary/40"
            placeholder="Shown when the survey link has expired."
            maxLength={5000}
          />
        </div>
        <div>
          <Label>Invalid / unknown link message *</Label>
          <textarea
            value={notFoundMessage}
            onChange={(e) => setNotFoundMessage(e.target.value)}
            rows={3}
            className="w-full resize-y rounded-md border border-glass-strong bg-glass px-3 py-2 text-sm text-foreground outline-none focus:border-primary/40"
            placeholder="Shown when the token does not match a known survey."
            maxLength={5000}
          />
        </div>
        <div>
          <Label>Heading above the per-agent block (optional)</Label>
          <Input
            value={agentBlockHeading}
            onChange={(e) => setAgentBlockHeading(e.target.value)}
            placeholder="Empty = no heading. Only shown when this survey has per-agent questions."
            maxLength={500}
          />
        </div>
      </section>

      <QuestionListSection
        title={`Survey questions (${surveyQuestions.length})`}
        helpText="Asked once per submission. Add a free-text question here if you want a generic comment field."
        questions={surveyQuestions}
        setQuestions={setSurveyQuestions}
      />

      <QuestionListSection
        title={`Per-agent questions (${agentQuestions.length})`}
        helpText="Repeated for every contributing agent on the ticket. Leave empty to skip the per-agent block."
        questions={agentQuestions}
        setQuestions={setAgentQuestions}
      />
    </div>
  );
}

function QuestionListSection({
  title,
  helpText,
  questions,
  setQuestions,
}: {
  title: string;
  helpText: string;
  questions: DraftQuestion[];
  setQuestions: React.Dispatch<React.SetStateAction<DraftQuestion[]>>;
}) {
  return (
    <section className="flex flex-col gap-3 rounded-lg border border-glass-strong bg-glass p-5">
      <header className="flex items-center justify-between">
        <div>
          <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            {title}
          </h2>
          <p className="text-xs text-muted-foreground">{helpText}</p>
        </div>
        <Button
          size="sm"
          onClick={() => setQuestions((qs) => [...qs, emptyDraft()])}
        >
          <Plus className="h-4 w-4" />
          Add question
        </Button>
      </header>
      {questions.length === 0 && (
        <p className="text-sm text-muted-foreground">
          No questions yet.
        </p>
      )}
      <ul className="flex flex-col gap-3">
        {questions.map((q, i) => (
          <QuestionRow
            key={q.clientId}
            question={q}
            index={i}
            total={questions.length}
            onChange={(next) =>
              setQuestions((qs) =>
                qs.map((x) => (x.clientId === q.clientId ? next : x)),
              )
            }
            onRemove={() =>
              setQuestions((qs) => qs.filter((x) => x.clientId !== q.clientId))
            }
            onMove={(dir) =>
              setQuestions((qs) => {
                const next = qs.slice();
                const j = i + dir;
                if (j < 0 || j >= next.length) return qs;
                [next[i], next[j]] = [next[j]!, next[i]!];
                return next;
              })
            }
          />
        ))}
      </ul>
    </section>
  );
}

function Label({
  children,
  className = "",
}: {
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div
      className={`mb-1 text-xs uppercase tracking-wider text-muted-foreground ${className}`}
    >
      {children}
    </div>
  );
}

function QuestionRow({
  question,
  index,
  total,
  onChange,
  onRemove,
  onMove,
}: {
  question: DraftQuestion;
  index: number;
  total: number;
  onChange: (q: DraftQuestion) => void;
  onRemove: () => void;
  onMove: (direction: 1 | -1) => void;
}) {
  return (
    <li className="flex flex-col gap-3 rounded-md border border-glass-strong bg-glass p-3">
      <div className="flex items-start gap-3">
        <div className="flex flex-col gap-1 pt-1">
          <button
            type="button"
            aria-label="Move up"
            onClick={() => onMove(-1)}
            disabled={index === 0}
            className="rounded-md p-1 text-muted-foreground hover:bg-glass-hover disabled:opacity-30"
          >
            <ArrowUp className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            aria-label="Move down"
            onClick={() => onMove(1)}
            disabled={index === total - 1}
            className="rounded-md p-1 text-muted-foreground hover:bg-glass-hover disabled:opacity-30"
          >
            <ArrowDown className="h-3.5 w-3.5" />
          </button>
        </div>
        <div className="flex-1 space-y-3">
          <div className="grid gap-2 md:grid-cols-3">
            <div className="md:col-span-2">
              <Label>Question</Label>
              <Input
                value={question.label}
                onChange={(e) => onChange({ ...question, label: e.target.value })}
                placeholder="e.g. How quickly was your issue resolved?"
                maxLength={500}
              />
            </div>
            <div>
              <Label>Type</Label>
              <Select
                value={question.type}
                onValueChange={(v) =>
                  onChange({ ...question, type: v as SurveyQuestionType })
                }
              >
                <SelectTrigger className="h-9">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Rating">Rating scale</SelectItem>
                  <SelectItem value="Nps">NPS (0–10)</SelectItem>
                  <SelectItem value="Text">Free text</SelectItem>
                  <SelectItem value="SingleChoice">Single choice</SelectItem>
                  <SelectItem value="MultiChoice">Multiple choice</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
          <div>
            <Label>Help text (optional)</Label>
            <Input
              value={question.helpText}
              onChange={(e) =>
                onChange({ ...question, helpText: e.target.value })
              }
              placeholder="Optional hint shown below the question"
              maxLength={2000}
            />
          </div>

          {question.type === "Rating" && (
            <div className="grid gap-2 md:grid-cols-2">
              <div>
                <Label>Number of points (2–10)</Label>
                <Input
                  type="number"
                  min={2}
                  max={10}
                  value={question.ratingPoints}
                  onChange={(e) =>
                    onChange({
                      ...question,
                      ratingPoints: Math.max(
                        2,
                        Math.min(10, Number(e.target.value) || 5),
                      ),
                    })
                  }
                />
              </div>
              <div>
                <Label>
                  Labels per point (optional, pipe-separated)
                </Label>
                <Input
                  value={question.ratingLabels}
                  onChange={(e) =>
                    onChange({ ...question, ratingLabels: e.target.value })
                  }
                  placeholder="Slecht | Tevreden | Zeer tevreden"
                  maxLength={500}
                />
              </div>
            </div>
          )}

          {(question.type === "SingleChoice" || question.type === "MultiChoice") && (
            <div>
              <Label>Options</Label>
              <ul className="flex flex-col gap-1.5">
                {question.choiceOptions.map((o, idx) => (
                  <li key={idx} className="flex items-center gap-2">
                    <Input
                      value={o.value}
                      onChange={(e) => {
                        const next = [...question.choiceOptions];
                        next[idx] = { ...o, value: e.target.value };
                        onChange({ ...question, choiceOptions: next });
                      }}
                      placeholder="value"
                      className="w-32"
                      maxLength={120}
                    />
                    <Input
                      value={o.label}
                      onChange={(e) => {
                        const next = [...question.choiceOptions];
                        next[idx] = { ...o, label: e.target.value };
                        onChange({ ...question, choiceOptions: next });
                      }}
                      placeholder="Label shown to the customer"
                      maxLength={200}
                    />
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => {
                        const next = question.choiceOptions.filter(
                          (_, j) => j !== idx,
                        );
                        onChange({ ...question, choiceOptions: next });
                      }}
                      aria-label={`Remove option ${idx + 1}`}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </li>
                ))}
              </ul>
              <Button
                variant="ghost"
                size="sm"
                onClick={() =>
                  onChange({
                    ...question,
                    choiceOptions: [
                      ...question.choiceOptions,
                      { value: "", label: "" },
                    ],
                  })
                }
                className="mt-2"
              >
                <Plus className="h-4 w-4" />
                Add option
              </Button>
            </div>
          )}

          <div className="flex items-center justify-between rounded-md border border-glass-strong bg-glass px-3 py-2">
            <Label className="mb-0">Required</Label>
            <Switch
              checked={question.isRequired}
              onCheckedChange={(v) => onChange({ ...question, isRequired: v })}
            />
          </div>
        </div>
        <Button
          variant="ghost"
          size="sm"
          onClick={onRemove}
          aria-label="Remove question"
        >
          <Trash2 className="h-4 w-4" />
        </Button>
      </div>
    </li>
  );
}
