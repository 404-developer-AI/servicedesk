import * as React from "react";
import { Link } from "@tanstack/react-router";
import { FlaskConical, PhoneCall, User } from "lucide-react";
import type { TicketListItem } from "@/lib/ticket-api";
import { colorPillStyle } from "@/lib/colorPill";
import { ticketRowAccentStyle } from "@/lib/ticketFlags";
import { useTheme } from "@/app/ThemeProvider";
import { emailLocalPart, relativeAge, requesterName } from "../format";

/// One ticket as a card — the mobile replacement for a ticket-table row.
/// Same accent rules as the desktop row (priority > Call-back > Research,
/// lib/ticketFlags.ts), the same theme-aware status pill, and the fields an
/// agent needs to decide whether to open it: number, status, subject,
/// requester/company, assignee and age.
export const TicketCard = React.memo(function TicketCard({
  ticket: t,
  flagColors,
  serverNowMs,
  zone,
}: {
  ticket: TicketListItem;
  flagColors: { callback: string; research: string };
  serverNowMs: number | null;
  zone: string | null;
}) {
  const { family, mode } = useTheme();
  const requester = requesterName(t.requesterFirstName, t.requesterLastName, t.requesterEmail);
  const assignee = emailLocalPart(t.assigneeEmail);
  return (
    <Link
      to="/m/tickets/$ticketId"
      params={{ ticketId: t.id }}
      className="sd-m-card glass-card block overflow-hidden transition-transform active:scale-[0.99]"
    >
      {/* The accent (inset bar + glow) sits on an inner layer so the card
          keeps its own theme shadow. */}
      <div className="px-4 py-3.5" style={ticketRowAccentStyle(t, flagColors)}>
        <div className="flex items-center gap-2">
          <span className="font-mono text-[12px] text-muted-foreground">#{t.number}</span>
          {t.isCallback && <PhoneCall className="h-3.5 w-3.5" style={{ color: flagColors.callback }} aria-label="Call back" />}
          {t.isResearch && <FlaskConical className="h-3.5 w-3.5" style={{ color: flagColors.research }} aria-label="Research" />}
          {!t.priorityIsDefault && (
            <span
              className="rounded-full border px-2 py-0.5 text-[11px] font-medium"
              style={colorPillStyle(t.priorityColor || "#6b7280", { family, mode })}
            >
              {t.priorityName}
            </span>
          )}
          <span className="ml-auto shrink-0 text-[12px] tabular-nums text-muted-foreground">
            {relativeAge(t.updatedUtc, serverNowMs, zone)}
          </span>
        </div>
        <p className="mt-1.5 line-clamp-2 text-[15px] font-semibold leading-snug text-foreground">{t.subject || "(no subject)"}</p>
        <div className="mt-2 flex items-center gap-2">
          <span
            className="shrink-0 rounded-full border px-2 py-0.5 text-[11px] font-medium"
            style={colorPillStyle(t.statusColor || "#6b7280", { family, mode })}
          >
            {t.statusName}
          </span>
          <span className="min-w-0 flex-1 truncate text-[13px] text-muted-foreground">
            {requester}
            {t.companyName ? ` · ${t.companyName}` : ""}
          </span>
          {assignee && (
            <span className="inline-flex max-w-[34%] shrink-0 items-center gap-1 truncate text-[12px] text-muted-foreground">
              <User className="h-3 w-3 shrink-0" />
              <span className="truncate">{assignee}</span>
            </span>
          )}
        </div>
      </div>
    </Link>
  );
});
