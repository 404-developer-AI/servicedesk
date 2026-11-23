import { Download, GitBranch } from "lucide-react";
import { Link } from "@tanstack/react-router";
import { formatDate } from "@/lib/dateFormat";
import { formatBytes } from "@/lib/format";
import type { Ticket } from "@/lib/ticket-api";

export function DescriptionAttachmentChip({
  attachment: a,
}: {
  attachment: { id: string; name: string; mimeType: string; size: number; url: string };
}) {
  return (
    <a
      href={a.url}
      target="_blank"
      rel="noreferrer"
      className="inline-flex items-center gap-2 rounded-md border border-border/60 bg-background/40 px-2.5 py-1.5 text-xs text-foreground/90 hover:border-primary/50 hover:bg-primary/10"
      title={`${a.mimeType} · ${formatBytes(a.size)}`}
    >
      <Download className="h-3.5 w-3.5 text-primary" />
      <span className="max-w-[220px] truncate">{a.name}</span>
      <span className="text-muted-foreground">{formatBytes(a.size)}</span>
    </a>
  );
}

export function SplitBanners({
  ticket,
  splitFromTicketNumber,
  splitFromUserName,
  splitChildren,
}: {
  ticket: Ticket;
  splitFromTicketNumber: string | null;
  splitFromUserName: string | null;
  splitChildren: { id: string; number: number }[];
}) {
  const isSplit = !!ticket.splitFromTicketId;
  const hasChildren = splitChildren.length > 0;
  if (!isSplit && !hasChildren) return null;

  return (
    <div className="shrink-0 pb-3 space-y-2">
      {isSplit && ticket.splitFromTicketId && (
        <div className="rounded-md border border-sky-400/30 bg-sky-500/6 px-3 py-2.5 flex items-start gap-2">
          <GitBranch className="h-4 w-4 shrink-0 mt-0.5 text-sky-300/90" />
          <div className="text-sm text-sky-100/90">
            This ticket was split from{" "}
            <Link
              to="/tickets/$ticketId"
              params={{ ticketId: ticket.splitFromTicketId }}
              className="font-medium underline underline-offset-2 hover:text-sky-50"
            >
              #{splitFromTicketNumber ?? "?"}
            </Link>
            {ticket.splitFromUtc && (
              <>
                {" "}on {formatDate(ticket.splitFromUtc)}
              </>
            )}
            {splitFromUserName && (
              <>
                {" "}by <span className="text-sky-50/90">{splitFromUserName}</span>
              </>
            )}
            .
          </div>
        </div>
      )}
      {hasChildren && (
        <div className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground/80 flex items-center gap-2 flex-wrap">
          <GitBranch className="h-3.5 w-3.5 shrink-0 text-sky-300/80" />
          <span>Split into</span>
          {splitChildren.map((child, idx) => (
            <span key={child.id}>
              <Link
                to="/tickets/$ticketId"
                params={{ ticketId: child.id }}
                className="text-foreground/80 font-medium hover:text-foreground hover:underline underline-offset-2"
              >
                #{child.number}
              </Link>
              {idx < splitChildren.length - 1 ? "," : ""}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
