import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Building2, Clock, FileText, Lock, MessageSquare, Paperclip, User } from "lucide-react";
import { toast } from "sonner";
import { SafeHtml } from "@/components/SafeHtml";
import { ApiError, apiErrorMessage } from "@/lib/api";
import { portalTicketApi, type PortalMessage } from "@/lib/portal-api";
import { cn } from "@/lib/utils";
import { Eye } from "lucide-react";
import { PortalComposer, htmlHasText, type PendingFile } from "@/portal/PortalComposer";
import { PriorityDot, StatusPill, formatBytes, usePortalCompany, usePortalDates, usePortalMe } from "@/portal/portalShared";

export function PortalTicketDetailPage({ ticketId }: { ticketId: string }) {
  const qc = useQueryClient();
  const dates = usePortalDates();
  const detail = useQuery({
    queryKey: ["portal", "ticket", ticketId],
    queryFn: () => portalTicketApi.get(ticketId),
    retry: (count, err) => !(err instanceof ApiError && err.status === 404) && count < 2,
  });
  const [reply, setReply] = useState("");
  const [files, setFiles] = useState<PendingFile[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  // A deep link may open a ticket of another of the customer's companies;
  // follow it with the header switcher so "back to tickets" stays coherent.
  const company = usePortalCompany();
  const me = usePortalMe();
  const readOnly = me.user?.impersonated ?? false;
  const ticketCompanyId = detail.data?.ticket.companyId ?? null;
  useEffect(() => {
    if (ticketCompanyId && company.active && ticketCompanyId !== company.active.id && company.companies.some((c) => c.id === ticketCompanyId)) {
      company.select(ticketCompanyId);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ticketCompanyId]);

  async function submitReply() {
    if (!htmlHasText(reply)) {
      toast.error("Write a message first.");
      return;
    }
    setBusy("Posting…");
    try {
      const { eventId } = await portalTicketApi.reply(ticketId, reply);
      let failed = 0;
      for (let i = 0; i < files.length; i++) {
        setBusy(`Uploading ${i + 1} of ${files.length}…`);
        try {
          await portalTicketApi.upload(ticketId, eventId, files[i]!.file);
        } catch (e) {
          failed++;
          toast.error(`${files[i]!.file.name}: ${apiErrorMessage(e) ?? "upload failed"}`);
        }
      }
      setReply("");
      setFiles([]);
      toast.success(failed ? "Reply posted, some attachments failed." : "Reply posted.");
      await qc.invalidateQueries({ queryKey: ["portal", "ticket", ticketId] });
      await qc.invalidateQueries({ queryKey: ["portal", "tickets"] });
    } catch (e) {
      toast.error(apiErrorMessage(e) ?? "Could not post your reply.");
      if (e instanceof ApiError && e.status === 409) await qc.invalidateQueries({ queryKey: ["portal", "ticket", ticketId] });
    } finally {
      setBusy(null);
    }
  }

  if (detail.isLoading) {
    return (
      <div className="space-y-4">
        <div className="h-6 w-40 animate-pulse rounded bg-glass" />
        <div className="h-28 animate-pulse rounded-[var(--radius)] bg-glass" />
        <div className="h-40 animate-pulse rounded-[var(--radius)] bg-glass" />
      </div>
    );
  }
  if (detail.isError || !detail.data) {
    return (
      <div className="glass-card p-8 text-center">
        <p className="text-sm font-medium">This ticket does not exist or is not visible to you.</p>
        <Link to="/portal" className="mt-3 inline-flex items-center gap-1.5 text-xs text-primary hover:underline">
          <ArrowLeft className="h-3.5 w-3.5" /> Back to my tickets
        </Link>
      </div>
    );
  }

  const { ticket, messages, canReply, replyBlockedReason } = detail.data;

  return (
    <div className="space-y-5" data-testid="portal-ticket-detail">
      <Link to="/portal" className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground">
        <ArrowLeft className="h-3.5 w-3.5" /> My tickets
      </Link>

      <header className="glass-card p-5 sm:p-6">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2 font-mono text-[11px] text-muted-foreground">#{ticket.number}</div>
            <h1 className="mt-1 font-display text-display-sm tracking-tight">{ticket.subject}</h1>
          </div>
          <StatusPill name={ticket.status.name} color={ticket.status.color} className="text-xs" />
        </div>
        <dl className="mt-4 grid grid-cols-2 gap-x-6 gap-y-2 text-xs sm:grid-cols-4">
          <Meta icon={User} label="Requester" value={ticket.requester.isYou ? "You" : ticket.requester.name || ticket.requester.email} />
          {ticket.companyName ? <Meta icon={Building2} label="Company" value={ticket.companyName} /> : null}
          <Meta icon={Clock} label="Opened" value={dates.dateTime(ticket.createdUtc)} />
          <Meta icon={Clock} label="Last update" value={dates.dateTime(ticket.updatedUtc)} />
          <div>
            <dt className="text-[10px] uppercase tracking-[0.14em] text-muted-foreground">Priority</dt>
            <dd className="mt-0.5">
              <PriorityDot name={ticket.priority.name} color={ticket.priority.color} />
            </dd>
          </div>
        </dl>
      </header>

      {(ticket.descriptionHtml || ticket.descriptionText) && ticket.source !== "Portal" ? (
        <section className="glass-card p-5">
          <h2 className="mb-2 flex items-center gap-2 text-xs font-medium uppercase tracking-[0.14em] text-muted-foreground">
            <FileText className="h-3.5 w-3.5" /> Description
          </h2>
          {ticket.descriptionHtml ? (
            <SafeHtml html={ticket.descriptionHtml} className="prose prose-sm max-w-none text-sm" />
          ) : (
            <p className="whitespace-pre-wrap text-sm">{ticket.descriptionText}</p>
          )}
        </section>
      ) : null}

      <section className="space-y-3">
        <h2 className="flex items-center gap-2 text-xs font-medium uppercase tracking-[0.14em] text-muted-foreground">
          <MessageSquare className="h-3.5 w-3.5" /> Conversation
        </h2>
        {messages.length === 0 ? (
          <div className="glass-card p-6 text-center text-sm text-muted-foreground">No messages yet.</div>
        ) : (
          <ol className="space-y-3">
            {messages.map((m) => (
              <MessageItem key={m.id} message={m} when={dates.dateTime(m.createdUtc)} />
            ))}
          </ol>
        )}
      </section>

      <section className="glass-card p-5" data-testid="portal-reply">
        {readOnly ? (
          <div className="flex items-start gap-2.5 text-sm text-muted-foreground">
            <Eye className="mt-0.5 h-4 w-4 shrink-0" />
            <p>Read-only view — replying is disabled while viewing the portal as this customer.</p>
          </div>
        ) : canReply ? (
          <>
            <h2 className="mb-3 text-sm font-medium">Reply</h2>
            <PortalComposer
              value={reply}
              onChange={setReply}
              files={files}
              onFilesChange={setFiles}
              placeholder="Write your reply…"
              disabled={busy !== null}
              busyLabel={busy}
              submitLabel="Send reply"
              onSubmit={submitReply}
            />
          </>
        ) : (
          <div className="flex items-start gap-2.5 text-sm text-muted-foreground">
            <Lock className="mt-0.5 h-4 w-4 shrink-0" />
            <p>
              {replyBlockedReason === "closed"
                ? "This ticket is closed and no longer accepts replies."
                : "This ticket is resolved and no longer accepts replies."}{" "}
              <Link to="/portal/tickets/new" className="font-medium text-primary hover:underline">
                Open a new ticket
              </Link>{" "}
              if you need further help.
            </p>
          </div>
        )}
      </section>
    </div>
  );
}

function Meta({ icon: Icon, label, value }: { icon: typeof User; label: string; value: string }) {
  return (
    <div className="min-w-0">
      <dt className="flex items-center gap-1 text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
        <Icon className="h-3 w-3" /> {label}
      </dt>
      <dd className="mt-0.5 truncate text-foreground">{value}</dd>
    </div>
  );
}

function MessageItem({ message, when }: { message: PortalMessage; when: string }) {
  if (message.type === "StatusChange" && message.statusChange) {
    return (
      <li className="flex items-center gap-3 px-1 text-xs text-muted-foreground">
        <span className="h-px flex-1 bg-glass" />
        <span>
          Status changed{message.statusChange.from ? ` from ${message.statusChange.from}` : ""} to{" "}
          <span className="font-medium text-foreground">{message.statusChange.to}</span> · {when}
        </span>
        <span className="h-px flex-1 bg-glass" />
      </li>
    );
  }
  const mine = message.isYou;
  const agent = message.kind === "agent";
  return (
    <li className={cn("glass-card overflow-hidden", agent && "sd-portal-agent-message border-primary/30")}>
      <div className={cn("flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-glass px-4 py-2 text-xs", agent ? "bg-primary/[0.06]" : "bg-glass")}>
        <span
          className={cn(
            "inline-flex h-6 w-6 items-center justify-center rounded-full text-[10px] font-semibold",
            agent ? "bg-primary text-primary-foreground" : "bg-glass-strong text-foreground",
          )}
          aria-hidden
        >
          {agent ? "SD" : (message.authorName || "?").slice(0, 1).toUpperCase()}
        </span>
        <span className="font-medium text-foreground">{mine ? "You" : message.authorName}</span>
        {message.type === "MailReceived" || message.type === "MailSent" ? (
          <span className="rounded border border-glass px-1.5 py-0.5 text-[10px] uppercase tracking-[0.12em] text-muted-foreground">mail</span>
        ) : null}
        <span className="ml-auto text-muted-foreground">{when}</span>
      </div>
      <div className="px-4 py-3">
        {message.bodyHtml ? (
          <SafeHtml html={message.bodyHtml} className="prose prose-sm max-w-none text-sm" />
        ) : (
          <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
        )}
        {message.attachments.length > 0 && (
          <ul className="mt-3 flex flex-wrap gap-2">
            {message.attachments.map((a) => (
              <li key={a.id ?? a.url ?? a.name ?? ""}>
                <a
                  href={a.url ?? "#"}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-1.5 rounded-md border border-glass bg-glass px-2.5 py-1 text-xs text-foreground hover:bg-glass-hover"
                >
                  <Paperclip className="h-3.5 w-3.5 text-muted-foreground" />
                  <span className="max-w-[240px] truncate">{a.name ?? "attachment"}</span>
                  <span className="text-muted-foreground">{formatBytes(a.size)}</span>
                </a>
              </li>
            ))}
          </ul>
        )}
      </div>
    </li>
  );
}
