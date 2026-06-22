import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlertCircle,
  ArrowLeft,
  Bot,
  CheckCircle2,
  DollarSign,
  KeyRound,
  MessageCircleQuestion,
  Settings2,
  Trash2,
  Users,
} from "lucide-react";
import { settingsApi, type SettingEntry } from "@/lib/api";
import {
  claudeAdminApi,
  type ClaudeConnectionState,
} from "@/lib/claude-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { SettingField } from "@/components/settings/SettingField";
import { cn } from "@/lib/utils";

const STATUS_QK = ["integrations", "claude", "status"] as const;
const SECRET_QK = ["integrations", "claude", "secret"] as const;
const USAGE_QK = ["integrations", "claude", "usage"] as const;
const SETTINGS_QK = ["settings", "list", "Claude AI"] as const;

const STATE_LABEL: Record<
  ClaudeConnectionState,
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
  NeedsZeroDataRetention: {
    tone: "border-amber-400/30 bg-amber-500/[0.08] text-amber-200",
    text: "ZDR confirmation required",
    dot: "bg-amber-400",
  },
  Ready: {
    tone: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    text: "Ready",
    dot: "bg-emerald-400",
  },
};

function findEntry(entries: SettingEntry[] | undefined, key: string) {
  return entries?.find((e) => e.key === key);
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

function microEurToDisplay(microEur: number): string {
  return `€${(microEur / 1_000_000).toFixed(2)}`;
}

function centsToDisplay(cents: number): string {
  return `€${(cents / 100).toFixed(2)}`;
}

export function ClaudeIntegrationPage() {
  const qc = useQueryClient();

  const statusQ = useQuery({ queryKey: STATUS_QK, queryFn: claudeAdminApi.status });
  const secretQ = useQuery({ queryKey: SECRET_QK, queryFn: claudeAdminApi.secretStatus });
  const usageQ = useQuery({ queryKey: USAGE_QK, queryFn: claudeAdminApi.getUsage });
  const settingsList = useQuery({
    queryKey: SETTINGS_QK,
    queryFn: () => settingsApi.list("Claude AI"),
  });

  const hasKey = secretQ.data?.configured ?? false;
  const [keyDraft, setKeyDraft] = useState("");

  const saveSecret = useMutation({
    mutationFn: () => claudeAdminApi.setSecret(keyDraft),
    onSuccess: () => {
      toast.success("API key saved");
      setKeyDraft("");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
    onError: () => toast.error("Failed to save API key"),
  });

  const deleteSecret = useMutation({
    mutationFn: () => claudeAdminApi.deleteSecret(),
    onSuccess: () => {
      toast.success("API key cleared");
      qc.invalidateQueries({ queryKey: SECRET_QK });
      qc.invalidateQueries({ queryKey: STATUS_QK });
    },
  });

  const state = statusQ.data?.state ?? "Disabled";
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
            <Bot className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Claude AI</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Generates draft internal notes for tickets using Anthropic's Claude API.
            Agents can attach screenshots; the output is always a draft — never
            auto-posted. Requires a zero-data-retention (ZDR) agreement with Anthropic.
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

      {/* ---- API Key ----------------------------------------- */}
      <section className="space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <KeyRound className="h-4 w-4 text-muted-foreground" />
          API key
        </div>
        <p className="text-xs text-muted-foreground/70">
          Anthropic API key. Stored encrypted via the DataProtection key-ring; never
          echoed back. Obtain one at{" "}
          <a
            href="https://console.anthropic.com"
            target="_blank"
            rel="noopener noreferrer"
            className="underline underline-offset-2 hover:text-foreground"
          >
            console.anthropic.com
          </a>
          .
        </p>
        <div className="flex flex-wrap gap-2">
          <Input
            type="password"
            autoComplete="new-password"
            placeholder={hasKey ? "Replace API key…" : "Paste API key…"}
            value={keyDraft}
            onChange={(e) => setKeyDraft(e.target.value)}
            className="h-9 w-80 bg-glass font-mono text-sm"
          />
          <Button
            size="sm"
            className="h-9"
            disabled={keyDraft.trim().length === 0 || saveSecret.isPending}
            onClick={() => saveSecret.mutate()}
          >
            {hasKey ? "Replace" : "Save"}
          </Button>
          {hasKey && (
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
          {hasKey ? (
            <span className="inline-flex items-center gap-1 text-xs text-emerald-300">
              <CheckCircle2 className="h-3 w-3" /> Key saved
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 text-xs text-muted-foreground/60">
              <AlertCircle className="h-3 w-3" /> No key configured
            </span>
          )}
        </div>
      </section>

      {/* ---- Gates ------------------------------------------- */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-3 flex items-center gap-2 text-sm font-medium text-foreground">
          <Settings2 className="h-4 w-4 text-muted-foreground" />
          Gates
        </div>
        {settingsList.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : (
          <>
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.Enabled")}
              queryKey={SETTINGS_QK}
              label="Enabled"
              hint="Master on/off switch. When disabled, the Ask AI button is hidden for all agents and no API calls are made."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.ZeroDataRetentionConfirmed")}
              queryKey={SETTINGS_QK}
              label="Zero data retention confirmed"
              hint="REQUIRED before use. Ticket content (subject, note bodies, attachment text) is sent to Anthropic. Enabling this confirms your Anthropic organisation is enrolled in the zero data retention (ZDR) programme so no prompt data is stored on Anthropic servers. Do not enable without verifying your ZDR status at console.anthropic.com."
            />
          </>
        )}
      </section>

      {/* ---- Connection -------------------------------------- */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-3 text-sm font-medium text-foreground">Connection</div>
        {settingsList.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : (
          <>
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.Model")}
              queryKey={SETTINGS_QK}
              label="Model"
              hint="Anthropic model ID, e.g. claude-opus-4-5 or claude-sonnet-4-5. Check the Anthropic docs for the latest model IDs."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.ApiBaseUrl")}
              queryKey={SETTINGS_QK}
              label="API base URL"
              hint="Leave blank to use the default Anthropic endpoint (https://api.anthropic.com). Override only for custom proxy or local test setups."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.MaxTokens")}
              queryKey={SETTINGS_QK}
              label="Max output tokens"
              hint="Maximum number of tokens Claude may generate per proposal. Higher values allow longer responses but cost more."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.Effort")}
              queryKey={SETTINGS_QK}
              label="Effort"
              hint="Reasoning effort level: off (no extended thinking), low, medium, or high. Higher effort improves quality but increases latency and cost."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.RequestTimeoutSeconds")}
              queryKey={SETTINGS_QK}
              label="Request timeout (seconds)"
              hint="How long to wait for a response from Anthropic before giving up with a 502 error."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.MaxContextChars")}
              queryKey={SETTINGS_QK}
              label="Max context chars"
              hint="Maximum number of characters from the ticket conversation included in the prompt. Prevents sending very large tickets that would exceed the model's context window or drive up cost."
            />
          </>
        )}
      </section>

      {/* ---- Prompt ------------------------------------------ */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-3 text-sm font-medium text-foreground">Prompt</div>
        {settingsList.isLoading ? (
          <Skeleton className="h-10 w-full bg-glass" />
        ) : (
          <FieldOrSkeleton
            entry={findEntry(settingsList.data, "Claude.SystemPrompt")}
            queryKey={SETTINGS_QK}
            label="System prompt"
            hint="Instruction sent to Claude before the ticket context. Customize to match your team's tone, language, and response style."
          />
        )}
      </section>

      {/* ---- Images ------------------------------------------ */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-3 text-sm font-medium text-foreground">Images</div>
        {settingsList.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : (
          <>
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.ImagesEnabled")}
              queryKey={SETTINGS_QK}
              label="Images enabled"
              hint="When enabled, agents can attach screenshot attachments from the ticket to the AI proposal request. Images are sent to Anthropic as base64 inline content."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.MaxImages")}
              queryKey={SETTINGS_QK}
              label="Max images per request"
              hint="Maximum number of screenshots an agent can attach to a single proposal request."
            />
          </>
        )}
      </section>

      {/* ---- Knowledge-base chat ----------------------------- */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-1 flex items-center gap-2 text-sm font-medium text-foreground">
          <MessageCircleQuestion className="h-4 w-4 text-muted-foreground" />
          Knowledge-base chat
        </div>
        <p className="mb-3 max-w-2xl text-xs text-muted-foreground/70">
          A floating chat button for agents that answers strictly from the knowledge
          base each agent may already see, via a single auth-scoped search tool — no
          internet, no other data. Shares the API key, ZDR gate and per-agent budget
          above; uses its own model and prompt. Hidden unless this is on and the agent
          has knowledge-base access.
        </p>
        {settingsList.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : (
          <>
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatEnabled")}
              queryKey={SETTINGS_QK}
              label="Enabled"
              hint="Master on/off switch for the floating knowledge-base chat. Independent of the ticket-assist switch above. When off the button is hidden for all agents and the chat endpoint refuses."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatModel")}
              queryKey={SETTINGS_QK}
              label="Model"
              hint="Anthropic model ID used for chat turns. Separate from the proposal model so the higher-volume chat can run a cheaper model — a Haiku-class model is recommended."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatMaxTokens")}
              queryKey={SETTINGS_QK}
              label="Max output tokens"
              hint="Maximum tokens the assistant may generate per chat turn. The reply is meant to be short (point at the article), so this can stay low."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatSystemPrompt")}
              queryKey={SETTINGS_QK}
              label="System prompt"
              hint="Scopes the assistant to the knowledge base and enforces strict grounding (answer only from retrieved articles; otherwise say nothing was found). Edit for tone/format; the user's message and article text are always sent as data, never as instructions."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatResultLimit")}
              queryKey={SETTINGS_QK}
              label="Search result limit"
              hint="Maximum knowledge-base articles returned per search (top-K). Bounds the per-turn input cost. Clamped to 1–20."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatMaxSearches")}
              queryKey={SETTINGS_QK}
              label="Max searches per turn"
              hint="Hard ceiling on how many times the assistant may search the knowledge base in one turn — bounds per-turn calls and cost. Clamped to 1–8."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatHistoryWindow")}
              queryKey={SETTINGS_QK}
              label="History window"
              hint="How many prior messages are carried into a turn (the rolling window). Older messages are dropped to bound input cost. Clamped to 2–50."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.KbChatSnippetChars")}
              queryKey={SETTINGS_QK}
              label="Snippet length (chars)"
              hint="Maximum characters of an article's body included in a search result sent to the model. Bounds per-result input cost. Clamped to 200–8000."
            />
          </>
        )}
      </section>

      {/* ---- Cost & budget ----------------------------------- */}
      <section className="space-y-1 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="mb-3 flex items-center gap-2 text-sm font-medium text-foreground">
          <DollarSign className="h-4 w-4 text-muted-foreground" />
          Cost &amp; budget
        </div>
        {settingsList.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : (
          <>
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.DefaultMonthlyBudgetEurCents")}
              queryKey={SETTINGS_QK}
              label="Default monthly budget (euro cents)"
              hint="Default per-agent monthly spend cap in euro cents (e.g. 500 = €5.00). Agents without a personal override use this value. Set to 0 for no limit."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.InputPriceCentsPerMTok")}
              queryKey={SETTINGS_QK}
              label="Input price (cents per million tokens)"
              hint="Used for cost tracking only — does not affect Anthropic billing. Set to match your Anthropic plan's per-MTok input price."
            />
            <FieldOrSkeleton
              entry={findEntry(settingsList.data, "Claude.OutputPriceCentsPerMTok")}
              queryKey={SETTINGS_QK}
              label="Output price (cents per million tokens)"
              hint="Used for cost tracking only. Set to match your Anthropic plan's per-MTok output price."
            />
          </>
        )}
      </section>

      {/* ---- Usage overview ---------------------------------- */}
      <section className="space-y-4 rounded-xl border border-glass-strong bg-glass p-5">
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Users className="h-4 w-4 text-muted-foreground" />
          Usage this month
        </div>
        {usageQ.isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full bg-glass" />
            <Skeleton className="h-10 w-full bg-glass" />
          </div>
        ) : usageQ.data ? (
          <>
            <p className="text-xs text-muted-foreground/70">
              Default monthly budget:{" "}
              <span className="font-mono text-foreground">
                {centsToDisplay(usageQ.data.defaultBudgetCents)}
              </span>
              {" "}per agent. Override per row below (leave blank to use default).
            </p>
            <UsageTable
              agents={usageQ.data.agents}
              defaultBudgetCents={usageQ.data.defaultBudgetCents}
              usageQueryKey={USAGE_QK}
            />
          </>
        ) : (
          <p className="text-xs text-muted-foreground/60 italic">No usage data available.</p>
        )}
      </section>
    </div>
  );
}

function UsageTable({
  agents,
  defaultBudgetCents,
  usageQueryKey,
}: {
  agents: import("@/lib/claude-api").ClaudeUsageAgent[];
  defaultBudgetCents: number;
  usageQueryKey: readonly unknown[];
}) {
  const qc = useQueryClient();

  const setBudget = useMutation({
    mutationFn: ({ userId, cents }: { userId: string; cents: number | null }) =>
      claudeAdminApi.setAgentBudget(userId, cents),
    onSuccess: () => {
      toast.success("Budget updated");
      qc.invalidateQueries({ queryKey: usageQueryKey });
    },
    onError: () => toast.error("Failed to update budget"),
  });

  if (agents.length === 0) {
    return (
      <p className="text-xs text-muted-foreground/60 italic">
        No agents have used the AI feature this month.
      </p>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-glass text-left text-xs text-muted-foreground/60 uppercase tracking-wider">
            <th className="pb-2 pr-4 font-medium">Agent</th>
            <th className="pb-2 pr-4 font-medium">Role</th>
            <th className="pb-2 pr-4 font-medium text-right">This month</th>
            <th className="pb-2 pr-4 font-medium text-right">Calls</th>
            <th className="pb-2 font-medium text-right">Monthly budget (€)</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-glass">
          {agents.map((agent) => (
            <UsageRow
              key={agent.userId}
              agent={agent}
              defaultBudgetCents={defaultBudgetCents}
              onSaveBudget={(cents) => setBudget.mutate({ userId: agent.userId, cents })}
              busy={setBudget.isPending}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function UsageRow({
  agent,
  defaultBudgetCents,
  onSaveBudget,
  busy,
}: {
  agent: import("@/lib/claude-api").ClaudeUsageAgent;
  defaultBudgetCents: number;
  onSaveBudget: (cents: number | null) => void;
  busy: boolean;
}) {
  const initialEuros =
    agent.budgetOverrideCents !== null
      ? String(agent.budgetOverrideCents / 100)
      : "";
  const [budgetInput, setBudgetInput] = useState(initialEuros);

  const effectiveBudgetDisplay = centsToDisplay(agent.effectiveBudgetCents);
  const spendDisplay = microEurToDisplay(agent.monthSpendMicroEur);

  const handleSave = () => {
    const trimmed = budgetInput.trim();
    if (trimmed === "") {
      onSaveBudget(null);
    } else {
      const euros = parseFloat(trimmed);
      if (!isFinite(euros) || euros < 0) {
        toast.error("Enter a valid positive amount in euros, or leave blank for default");
        return;
      }
      onSaveBudget(Math.round(euros * 100));
    }
  };

  const budgetOverDefault = defaultBudgetCents > 0
    ? agent.effectiveBudgetCents > 0
      ? agent.monthSpendMicroEur / 1_000_000 / (agent.effectiveBudgetCents / 100)
      : 0
    : null;

  return (
    <tr className="text-sm">
      <td className="py-3 pr-4">
        <span className="font-medium text-foreground">{agent.email}</span>
      </td>
      <td className="py-3 pr-4">
        <span className="text-xs uppercase tracking-wider text-muted-foreground/60">
          {agent.role}
        </span>
      </td>
      <td className="py-3 pr-4 text-right font-mono">
        <span
          className={cn(
            budgetOverDefault !== null && budgetOverDefault > 0.9
              ? "text-amber-300"
              : "text-foreground",
          )}
        >
          {spendDisplay}
        </span>
        {agent.effectiveBudgetCents > 0 && (
          <span className="ml-1 text-xs text-muted-foreground/50">
            / {effectiveBudgetDisplay}
          </span>
        )}
      </td>
      <td className="py-3 pr-4 text-right font-mono text-muted-foreground">
        {agent.callCount}
      </td>
      <td className="py-3 text-right">
        <div className="inline-flex items-center gap-1.5">
          <Input
            type="number"
            min="0"
            step="0.01"
            placeholder={`${(defaultBudgetCents / 100).toFixed(2)} (default)`}
            value={budgetInput}
            onChange={(e) => setBudgetInput(e.target.value)}
            onBlur={handleSave}
            onKeyDown={(e) => {
              if (e.key === "Enter") e.currentTarget.blur();
              if (e.key === "Escape") {
                setBudgetInput(initialEuros);
                e.currentTarget.blur();
              }
            }}
            disabled={busy}
            className="h-8 w-32 bg-glass text-right font-mono text-sm"
          />
          {agent.budgetOverrideCents !== null && (
            <span className="text-[10px] text-muted-foreground/50 italic">override</span>
          )}
        </div>
      </td>
    </tr>
  );
}
