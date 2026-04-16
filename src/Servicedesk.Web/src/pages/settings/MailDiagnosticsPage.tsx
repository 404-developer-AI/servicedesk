import * as React from "react";
import { Paperclip, Search } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { useServerTime, toServerLocal, formatUtcSuffix } from "@/hooks/useServerTime";
import {
  ApiError,
  mailDiagnosticsApi,
  type MailAttachmentDiagnostic,
  type MailAttachmentDiagnosticItem,
  type MailAttachmentSummary,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const STATE_BADGE: Record<string, string> = {
  Pending: "border-amber-400/30 bg-amber-500/10 text-amber-300",
  Ready: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
  Failed: "border-rose-400/40 bg-rose-500/10 text-rose-300",
  Stored: "border-sky-400/30 bg-sky-500/10 text-sky-300",
};

const JOB_BADGE: Record<string, string> = {
  Pending: "border-amber-400/30 bg-amber-500/10 text-amber-300",
  Running: "border-sky-400/30 bg-sky-500/10 text-sky-300",
  Succeeded: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
  Failed: "border-rose-400/40 bg-rose-500/10 text-rose-300",
  DeadLettered: "border-rose-500/40 bg-rose-600/15 text-rose-200",
};

export function MailDiagnosticsPage() {
  const { time: serverTime } = useServerTime();
  const offset = serverTime?.offsetMinutes ?? 0;
  const [input, setInput] = React.useState("");
  const [submitted, setSubmitted] = React.useState<string | null>(null);
  const [onlyIssues, setOnlyIssues] = React.useState(true);
  const [searchTerm, setSearchTerm] = React.useState("");

  const recent = useQuery({
    queryKey: ["admin", "mail-diagnostics", "list", onlyIssues],
    queryFn: () => mailDiagnosticsApi.list(onlyIssues, 50),
    refetchInterval: 15_000,
  });

  const query = useQuery({
    queryKey: ["admin", "mail-diagnostics", submitted],
    queryFn: () => mailDiagnosticsApi.get(submitted!),
    enabled: !!submitted && GUID_RE.test(submitted),
    retry: false,
  });

  const validGuid = GUID_RE.test(input.trim());

  const filteredRecent = React.useMemo(() => {
    const items = recent.data ?? [];
    const needle = searchTerm.trim().toLowerCase();
    if (!needle) return items;
    return items.filter(
      (m) =>
        m.subject?.toLowerCase().includes(needle) ||
        m.fromAddress?.toLowerCase().includes(needle) ||
        m.mailMessageId.toLowerCase().includes(needle),
    );
  }, [recent.data, searchTerm]);

  const pick = (mailId: string) => {
    setInput(mailId);
    setSubmitted(mailId);
  };

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Paperclip className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Mail diagnostics</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Read-only inspection of the attachment pipeline for a single ingested mail.
            Shows attachment rows, their ingest-job state and whether the blob actually
            landed on disk — useful when a ticket's inline image renders as "broken"
            or a file attachment never surfaces as a download link.
          </p>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <section className="glass-panel flex flex-col gap-3 p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <div className="text-sm font-medium text-foreground">Recent ingested mail</div>
            <div className="text-xs text-muted-foreground">
              Top 50 mails met attachments, nieuwste eerst. Klik om te inspecteren.
            </div>
          </div>
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <Switch checked={onlyIssues} onCheckedChange={setOnlyIssues} />
            Alleen met Pending/Failed
          </label>
        </div>
        <Input
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          placeholder="Zoek in subject / afzender / id"
          className="text-xs"
        />
        {recent.isLoading ? (
          <Skeleton className="h-24 w-full" />
        ) : filteredRecent.length === 0 ? (
          <div className="rounded-md border border-white/5 bg-white/[0.02] px-3 py-4 text-center text-xs text-muted-foreground">
            {onlyIssues
              ? "Geen mails met hangende of gefaalde bijlagen."
              : "Geen mails gevonden."}
          </div>
        ) : (
          <ul className="max-h-72 divide-y divide-white/5 overflow-y-auto rounded-md border border-white/5 bg-white/[0.02]">
            {filteredRecent.map((m) => (
              <li key={m.mailMessageId}>
                <button
                  type="button"
                  onClick={() => pick(m.mailMessageId)}
                  className={cn(
                    "flex w-full flex-col gap-1 px-3 py-2 text-left transition-colors hover:bg-white/[0.04]",
                    submitted === m.mailMessageId && "bg-white/[0.06]",
                  )}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="truncate text-sm text-foreground">
                      {m.subject || "(no subject)"}
                    </span>
                    <RecentBadges summary={m} />
                  </div>
                  <div className="flex flex-wrap gap-x-3 gap-y-0.5 text-[11px] text-muted-foreground">
                    <span>{m.fromAddress || "—"}</span>
                    <span>
                      {toServerLocal(m.receivedUtc, offset, true)}{" "}
                      <span className="text-muted-foreground/40">{formatUtcSuffix(m.receivedUtc)}</span>
                    </span>
                    <span className="font-mono">{m.mailMessageId}</span>
                  </div>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <form
        className="glass-panel flex flex-col gap-3 p-4 sm:flex-row sm:items-center"
        onSubmit={(e) => {
          e.preventDefault();
          if (validGuid) setSubmitted(input.trim());
        }}
      >
        <label className="flex-1 text-sm">
          <span className="text-muted-foreground">Mail message id (UUID from timeline metadata)</span>
          <Input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="e.g. 98e8828e-eaf0-41bb-b857-f9241ac11289"
            className="mt-1 font-mono text-xs"
          />
        </label>
        <Button type="submit" disabled={!validGuid} className="sm:self-end">
          <Search className="mr-2 h-4 w-4" />
          Inspect
        </Button>
      </form>

      {submitted && query.isLoading && (
        <div className="flex flex-col gap-3">
          <Skeleton className="h-20 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      )}

      {query.isError && (
        <div className="glass-panel border border-rose-500/20 p-4 text-sm text-rose-300">
          {query.error instanceof ApiError && query.error.status === 404
            ? "No mail row with that id."
            : `Failed to load diagnostics${query.error instanceof ApiError ? ` (${query.error.status})` : ""}.`}
        </div>
      )}

      {query.data && <DiagnosticsView data={query.data} offset={offset} />}
    </div>
  );
}

function RecentBadges({ summary }: { summary: MailAttachmentSummary }) {
  return (
    <div className="flex shrink-0 items-center gap-1 text-[10px]">
      {summary.readyCount > 0 && (
        <Badge className="border-emerald-400/30 bg-emerald-500/10 text-emerald-300">
          {summary.readyCount} ready
        </Badge>
      )}
      {summary.pendingCount > 0 && (
        <Badge className="border-amber-400/30 bg-amber-500/10 text-amber-300">
          {summary.pendingCount} pending
        </Badge>
      )}
      {summary.failedCount > 0 && (
        <Badge className="border-rose-400/40 bg-rose-500/10 text-rose-300">
          {summary.failedCount} failed
        </Badge>
      )}
    </div>
  );
}

function DiagnosticsView({ data, offset }: { data: MailAttachmentDiagnostic; offset: number }) {
  return (
    <div className="flex flex-col gap-4">
      <section className="glass-panel flex flex-col gap-2 p-4 text-sm">
        <div className="flex flex-wrap gap-x-6 gap-y-1">
          <Header label="Ticket">
            {data.ticketId ? (
              <span className="font-mono text-xs">{data.ticketId}</span>
            ) : (
              <span className="text-muted-foreground">—</span>
            )}
          </Header>
          <Header label="From">{data.fromAddress || "—"}</Header>
          <Header label="Received">
            {toServerLocal(data.receivedUtc, offset, true)}{" "}
            <span className="text-muted-foreground/40">{formatUtcSuffix(data.receivedUtc)}</span>
          </Header>
        </div>
        <Header label="Subject">{data.subject || "(no subject)"}</Header>
        <Header label="HTML body blob">
          {data.bodyHtmlBlobHash ? (
            <span className="flex items-center gap-2">
              <span className="font-mono text-[11px]">{data.bodyHtmlBlobHash.slice(0, 12)}…</span>
              <Badge
                className={
                  data.bodyHtmlBlobPresent
                    ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-300"
                    : "border-rose-400/40 bg-rose-500/10 text-rose-300"
                }
              >
                {data.bodyHtmlBlobPresent ? "present" : "missing on disk"}
              </Badge>
            </span>
          ) : (
            <span className="text-muted-foreground">none</span>
          )}
        </Header>
      </section>

      <section className="glass-panel overflow-hidden p-0 text-sm">
        <header className="flex items-center justify-between border-b border-white/5 px-4 py-3">
          <div className="font-medium text-foreground">
            Attachments ({data.attachments.length})
          </div>
          <div className="text-xs text-muted-foreground">
            {data.attachments.filter((a) => a.processingState === "Ready").length} ready ·{" "}
            {data.attachments.filter((a) => a.processingState === "Pending").length} pending ·{" "}
            {data.attachments.filter((a) => a.processingState === "Failed").length} failed
          </div>
        </header>
        {data.attachments.length === 0 ? (
          <div className="px-4 py-6 text-center text-sm text-muted-foreground">
            This mail has no attachment rows — Graph returned no file attachments for
            the message. If you expected an inline image, inspect the raw .eml to see
            whether it was sent as an attachment at all.
          </div>
        ) : (
          <ul className="divide-y divide-white/5">
            {data.attachments.map((a) => (
              <AttachmentRow key={a.id} attachment={a} offset={offset} />
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function AttachmentRow({
  attachment: a,
  offset,
}: {
  attachment: MailAttachmentDiagnosticItem;
  offset: number;
}) {
  const stateClass = STATE_BADGE[a.processingState] ?? "border-white/10 bg-white/5 text-muted-foreground";
  const jobClass = a.job ? JOB_BADGE[a.job.state] ?? "border-white/10 bg-white/5" : "";
  return (
    <li className="flex flex-col gap-2 px-4 py-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium text-foreground">{a.filename || "(no filename)"}</span>
        {a.isInline && (
          <Badge className="border border-purple-400/30 bg-purple-500/10 text-purple-300">inline</Badge>
        )}
        <Badge className={stateClass}>{a.processingState}</Badge>
        <Badge
          className={
            a.blobPresent
              ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-300"
              : "border-rose-400/40 bg-rose-500/10 text-rose-300"
          }
        >
          blob: {a.blobPresent ? "present" : a.contentHash ? "missing" : "n/a"}
        </Badge>
        {a.job && <Badge className={jobClass}>job: {a.job.state}</Badge>}
      </div>
      <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs text-muted-foreground sm:grid-cols-4">
        <Field label="Mime">{a.mimeType || "—"}</Field>
        <Field label="Size">{a.sizeBytes.toLocaleString()} B</Field>
        <Field label="Content-Id">{a.contentId || "—"}</Field>
        <Field label="Hash">
          {a.contentHash ? <span className="font-mono">{a.contentHash.slice(0, 12)}…</span> : "—"}
        </Field>
        {a.job && (
          <>
            <Field label="Attempts">{a.job.attemptCount}</Field>
            <Field label="Next attempt">
              {toServerLocal(a.job.nextAttemptUtc, offset, true)}{" "}
              <span className="text-muted-foreground/40">{formatUtcSuffix(a.job.nextAttemptUtc)}</span>
            </Field>
            <Field label="Updated">
              {toServerLocal(a.job.updatedUtc, offset, true)}{" "}
              <span className="text-muted-foreground/40">{formatUtcSuffix(a.job.updatedUtc)}</span>
            </Field>
            <Field label="Job id">{a.job.jobId}</Field>
          </>
        )}
      </div>
      {a.job?.lastError && (
        <pre className="whitespace-pre-wrap break-words rounded-md border border-rose-500/20 bg-rose-500/5 p-2 text-[11px] text-rose-200">
          {a.job.lastError}
        </pre>
      )}
    </li>
  );
}

function Header({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-baseline gap-2">
      <span className="text-xs uppercase tracking-[0.14em] text-muted-foreground">{label}</span>
      <span className="text-foreground">{children}</span>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col">
      <span className="text-[10px] uppercase tracking-[0.14em] text-muted-foreground/80">{label}</span>
      <span className="text-foreground/90">{children}</span>
    </div>
  );
}
