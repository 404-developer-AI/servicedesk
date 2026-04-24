import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import {
  ChevronDown,
  ChevronRight,
  Clock,
  Download,
  Loader2,
  Mail,
} from "lucide-react";
import { cn } from "@/lib/utils";
import {
  intakeFormsApi,
  type IntakeFormAgentView,
  type IntakeQuestion,
} from "@/lib/intakeForms-api";

type Props = {
  ticketId: string;
  instanceId: string;
  /// Optional — when provided, we use this instead of fetching (Sent/Expired
  /// events don't need the answers roundtrip).
  variant?: "submitted" | "sent" | "expired";
  templateName?: string | null;
  sentToEmail?: string | null;
};

/// Compact panel rendered under an IntakeForm* timeline event. For
/// "submitted" variant the agent sees a "View details" toggle that lazily
/// fetches the instance + answers and renders them as read-only Q&A rows.
/// "sent" and "expired" variants are one-liners (recipient + timestamp
/// context lives on the event dot itself).
export function IntakeSubmissionPanel({
  ticketId,
  instanceId,
  variant = "submitted",
  templateName,
  sentToEmail,
}: Props) {
  const [expanded, setExpanded] = React.useState(false);

  if (variant !== "submitted") {
    return (
      <div className="mt-1 flex items-center gap-2 text-xs text-muted-foreground">
        {templateName && (
          <span className="text-foreground/80">{templateName}</span>
        )}
        {variant === "sent" && sentToEmail && (
          <span className="flex items-center gap-1">
            <Mail className="h-3 w-3" />
            {sentToEmail}
          </span>
        )}
        {variant === "expired" && (
          <span className="flex items-center gap-1 text-orange-300/80">
            <Clock className="h-3 w-3" />
            Link no longer valid
          </span>
        )}
      </div>
    );
  }

  return (
    <div className="mt-1 space-y-2">
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={() => setExpanded((v) => !v)}
          className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
        >
          {expanded ? (
            <ChevronDown className="h-3 w-3" />
          ) : (
            <ChevronRight className="h-3 w-3" />
          )}
          {templateName ?? "Intake form"}
          <span className="text-muted-foreground/60">
            — {expanded ? "hide answers" : "view answers"}
          </span>
        </button>
        <a
          // Plain <a download> — the browser handles the file save using
          // the server's Content-Disposition filename. No JS blob buffering
          // and no extra tab; works identically on every modern browser.
          href={intakeFormsApi.pdfUrl(ticketId, instanceId)}
          download
          className="flex items-center gap-1 rounded-md border border-white/[0.06] bg-white/[0.02] px-2 py-0.5 text-[11px] text-muted-foreground hover:bg-white/[0.05] hover:text-foreground"
        >
          <Download className="h-3 w-3" />
          PDF
        </a>
      </div>

      {expanded && (
        <SubmissionDetails ticketId={ticketId} instanceId={instanceId} />
      )}
    </div>
  );
}

function SubmissionDetails({
  ticketId,
  instanceId,
}: {
  ticketId: string;
  instanceId: string;
}) {
  const q = useQuery({
    queryKey: ["intake-forms", "instance", ticketId, instanceId],
    queryFn: () => intakeFormsApi.getInstance(ticketId, instanceId),
  });

  if (q.isLoading) {
    return (
      <div className="flex items-center gap-2 rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-xs text-muted-foreground">
        <Loader2 className="h-3 w-3 animate-spin" />
        Loading answers…
      </div>
    );
  }
  if (q.isError) {
    return (
      <div className="rounded-md border border-red-500/30 bg-red-500/10 px-3 py-2 text-xs text-red-200">
        Could not load answers.
      </div>
    );
  }
  if (!q.data) return null;

  return <SubmissionTable view={q.data} />;
}

function SubmissionTable({ view }: { view: IntakeFormAgentView }) {
  const answers = (view.answers ?? {}) as Record<string, unknown>;
  const hasAny = Object.keys(answers).length > 0;

  return (
    <div className="rounded-md border border-white/[0.06] bg-white/[0.02] p-3">
      <div className="mb-2 flex items-center justify-between">
        <span className="text-[11px] font-medium uppercase tracking-widest text-muted-foreground/70">
          Submitted answers
        </span>
        {view.instance.submittedUtc && (
          <span className="text-[11px] text-muted-foreground/60">
            {new Date(view.instance.submittedUtc).toLocaleString()}
          </span>
        )}
      </div>
      {view.instance.sentToEmail && (
        <div className="mb-2 flex items-center gap-1 text-[11px] text-muted-foreground/60">
          <Mail className="h-3 w-3" />
          Sent to {view.instance.sentToEmail}
        </div>
      )}

      {!hasAny ? (
        <p className="text-xs text-muted-foreground">
          No answers recorded on this submission.
        </p>
      ) : (
        <dl className="flex flex-col gap-2">
          {view.template.questions
            .filter((q) => q.type !== "SectionHeader")
            .map((q) => {
              const raw = answers[q.id.toString()];
              return (
                <div
                  key={q.id}
                  className="grid gap-0.5 md:grid-cols-[auto,1fr] md:gap-3"
                >
                  <dt className="text-xs text-muted-foreground/80">
                    {q.label}
                  </dt>
                  <dd
                    className={cn(
                      "text-sm",
                      raw === undefined || raw === null || raw === ""
                        ? "text-muted-foreground/50"
                        : "text-foreground",
                    )}
                  >
                    {formatAnswer(q, raw)}
                  </dd>
                </div>
              );
            })}
        </dl>
      )}
    </div>
  );
}

function formatAnswer(q: IntakeQuestion, raw: unknown): string {
  if (raw === undefined || raw === null || raw === "") return "—";
  switch (q.type) {
    case "YesNo":
      return raw === true ? "Ja" : raw === false ? "Nee" : String(raw);
    case "DropdownSingle": {
      const opt = q.options.find((o) => o.value === raw);
      return opt?.label ?? String(raw);
    }
    case "DropdownMulti": {
      if (!Array.isArray(raw)) return String(raw);
      const labels = raw.map(
        (v) => q.options.find((o) => o.value === v)?.label ?? String(v),
      );
      return labels.length === 0 ? "—" : labels.join(", ");
    }
    case "Date":
      return typeof raw === "string" ? raw.slice(0, 10) : String(raw);
    default:
      return String(raw);
  }
}
