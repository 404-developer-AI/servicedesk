import { Link } from "@tanstack/react-router";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { formatDuration } from "@/lib/timesheet-api";
import { IntakeSubmissionPanel } from "@/components/intake/IntakeSubmissionPanel";
import type { TicketEvent } from "@/lib/ticket-api";

type Meta = Record<string, unknown>;

export function MetaChangeText({
  meta,
  fieldLabel,
}: {
  meta: Record<string, unknown>;
  fieldLabel: string;
}) {
  const from = meta.fromName ?? meta.from ?? meta.fromId;
  const to = meta.toName ?? meta.to ?? meta.toId;
  if (from && to) {
    return (
      <span className="text-sm text-muted-foreground">
        {fieldLabel} changed from{" "}
        <span className="text-foreground/80">{String(from)}</span> to{" "}
        <span className="text-foreground/80">{String(to)}</span>
      </span>
    );
  }
  if (to) {
    return (
      <span className="text-sm text-muted-foreground">
        {fieldLabel} set to{" "}
        <span className="text-foreground/80">{String(to)}</span>
      </span>
    );
  }
  return (
    <span className="text-sm text-muted-foreground">{fieldLabel} changed</span>
  );
}

export function AssignmentText({ meta }: { meta: Record<string, unknown> }) {
  const toName = meta.toName ?? meta.toEmail ?? meta.toUserId;
  if (!toName) {
    return (
      <span className="text-sm text-muted-foreground">Ticket unassigned</span>
    );
  }
  return (
    <span className="text-sm text-muted-foreground">
      Assigned to <span className="text-foreground/80">{String(toName)}</span>
    </span>
  );
}

/// v0.1.44 — "Pending till changed from X to Y", in the server's zone.
export function PendingTillChangeText({ from, to }: { from: unknown; to: unknown }) {
  const zone = useServerTimeZone();
  const fmt = (v: unknown) => (typeof v === "string" && v ? formatDateTime(v, zone) : "not set");
  return (
    <span className="text-sm text-muted-foreground">
      Pending till changed from <span className="text-foreground/80">{fmt(from)}</span>
      {" "}to <span className="text-foreground/80">{fmt(to)}</span>
    </span>
  );
}

export function CreatedBody({ meta }: { meta: Meta }) {
  const source = typeof meta.source === "string" ? meta.source : null;
  const via =
    source && source.toLowerCase() !== "web"
      ? ` via ${source.toLowerCase()}`
      : "";
  return (
    <span className="text-sm text-muted-foreground">
      Ticket created{via}
    </span>
  );
}

export function RequesterChangeBody({ meta }: { meta: Meta }) {
  const fromName =
    (typeof meta.fromName === "string" && meta.fromName.trim().length > 0)
      ? meta.fromName
      : (typeof meta.fromEmail === "string" ? meta.fromEmail : null);
  const toName =
    (typeof meta.toName === "string" && meta.toName.trim().length > 0)
      ? meta.toName
      : (typeof meta.toEmail === "string" ? meta.toEmail : null);
  const fromCompany = typeof meta.fromCompanyName === "string" ? meta.fromCompanyName : null;
  const toCompany = typeof meta.toCompanyName === "string" ? meta.toCompanyName : null;
  return (
    <span className="text-sm text-muted-foreground">
      Requester changed from{" "}
      <span className="text-foreground/80">{fromName ?? "unknown"}</span>
      {fromCompany && (
        <span className="text-muted-foreground/70"> ({fromCompany})</span>
      )}
      {" "}to{" "}
      <span className="text-foreground/80">{toName ?? "unknown"}</span>
      {toCompany && (
        <span className="text-muted-foreground/70"> ({toCompany})</span>
      )}
    </span>
  );
}

export function SystemNoteBody({ event }: { event: TicketEvent }) {
  return (
    <span className="text-sm italic text-muted-foreground">
      {event.bodyText ?? "System event"}
    </span>
  );
}

export function TimeLimitAlertDismissedBody({ meta }: { meta: Meta }) {
  const total = typeof meta.totalMinutes === "number" ? meta.totalMinutes : null;
  const limit = typeof meta.limitMinutes === "number" ? meta.limitMinutes : null;
  return (
    <span className="text-sm text-muted-foreground">
      Hour-limit warning dismissed
      {total !== null && limit !== null && (
        <span className="text-muted-foreground/70">
          {" "}
          ({formatDuration(total)} logged, limit {formatDuration(limit)})
        </span>
      )}
    </span>
  );
}

export function TimeLimitTrackingDisabledBody({ meta }: { meta: Meta }) {
  const total = typeof meta.totalMinutes === "number" ? meta.totalMinutes : null;
  const limit = typeof meta.limitMinutes === "number" ? meta.limitMinutes : null;
  return (
    <span className="text-sm text-muted-foreground">
      Hour tracking disabled for this ticket
      {total !== null && limit !== null && (
        <span className="text-muted-foreground/70">
          {" "}
          ({formatDuration(total)} logged, limit {formatDuration(limit)})
        </span>
      )}
      <span className="text-muted-foreground/70"> — see internal note for the reason</span>
    </span>
  );
}

export function TimeLimitExtendedBody({ meta }: { meta: Meta }) {
  const added = typeof meta.addedMinutes === "number" ? meta.addedMinutes : null;
  const newLimit =
    typeof meta.newLimitMinutes === "number" ? meta.newLimitMinutes : null;
  return (
    <span className="text-sm text-muted-foreground">
      Hour limit raised
      {added !== null && <span className="text-foreground/80"> by {formatDuration(added)}</span>}
      {newLimit !== null && (
        <span className="text-muted-foreground/70">
          {" "}
          (new limit {formatDuration(newLimit)})
        </span>
      )}
      <span className="text-muted-foreground/70"> — customer confirmation recorded</span>
    </span>
  );
}

export function ChecklistEventBody({ event, meta }: { event: TicketEvent; meta: Meta }) {
  const name = typeof meta.checklistName === "string" ? meta.checklistName : "Checklist";
  const itemCount = typeof meta.itemCount === "number" ? meta.itemCount : null;
  const done = typeof meta.requiredDone === "number" ? meta.requiredDone : null;
  const total = typeof meta.requiredTotal === "number" ? meta.requiredTotal : null;
  const itemTitle = typeof meta.itemTitle === "string" ? meta.itemTitle : null;
  const toState = typeof meta.toState === "string" ? meta.toState : null;
  const reason = typeof meta.reason === "string" && meta.reason ? meta.reason : null;
  const stateLabel =
    toState === "done" ? "ticked" : toState === "na" ? "marked not applicable" : toState === "open" ? "reopened" : "changed";
  return (
    <span className="text-sm text-muted-foreground">
      {event.eventType === "ChecklistAttached" && (
        <>
          Checklist <span className="text-foreground/80">“{name}”</span> added
          {itemCount !== null && <span className="text-muted-foreground/70"> ({itemCount} item{itemCount === 1 ? "" : "s"})</span>}
        </>
      )}
      {event.eventType === "ChecklistDetached" && (
        <>
          Checklist <span className="text-foreground/80">“{name}”</span> removed
          {done !== null && total !== null && total > 0 && (
            <span className="text-muted-foreground/70"> ({done}/{total} required done)</span>
          )}
        </>
      )}
      {event.eventType === "ChecklistCompleted" && (
        <>Checklist <span className="text-foreground/80">“{name}”</span> completed — every required item is done or not applicable</>
      )}
      {event.eventType === "ChecklistReopened" && (
        <>Checklist <span className="text-foreground/80">“{name}”</span> reopened — a required item was set back to open</>
      )}
      {event.eventType === "ChecklistItemChanged" && (
        <>
          <span className="text-foreground/80">“{itemTitle ?? "Item"}”</span> {stateLabel}
          <span className="text-muted-foreground/70"> · {name}</span>
          {reason && <span className="text-muted-foreground/70"> — {reason}</span>}
        </>
      )}
    </span>
  );
}

export function ProjectConvertedBody() {
  return (
    <span className="text-sm text-muted-foreground">
      Ticket converted to a project ticket
    </span>
  );
}

export function ProjectRevertedBody() {
  return (
    <span className="text-sm text-muted-foreground">
      Ticket converted back to a normal ticket
    </span>
  );
}

/// v0.1.67 — one line per finished campaign round on the project: how many
/// linked tickets got the mail (each mail sits on its own ticket).
export function CampaignSentBody({ meta }: { meta: Meta }) {
  const id = typeof meta.campaignId === "string" ? meta.campaignId : null;
  const name = typeof meta.campaignName === "string" ? meta.campaignName : "Campaign";
  const sent = Number(meta.sent ?? 0);
  const total = Number(meta.total ?? 0);
  const followUp = meta.kind === "FollowUp";
  return (
    <span className="text-sm text-muted-foreground">
      {followUp ? "Follow-up of " : ""}
      {id ? (
        <Link to="/campaigns/$campaignId" params={{ campaignId: id }} className="text-foreground/80 hover:text-primary">
          {name}
        </Link>
      ) : (
        <span className="text-foreground/80">{name}</span>
      )}{" "}
      sent to <span className="text-foreground/80 tabular-nums">{sent}/{total}</span> tickets
    </span>
  );
}

const CAMPAIGN_ANSWER_TEXT: Record<string, string> = {
  Approved: "approved the proposed moment",
  OtherTime: "asked for another moment",
  Declined: "declined",
};

/// v0.1.67 — the customer's answer through a campaign's answer link.
export function CampaignAnswerBody({ meta, note }: { meta: Meta; note: string | null | undefined }) {
  const choice = typeof meta.choice === "string" ? CAMPAIGN_ANSWER_TEXT[meta.choice] ?? meta.choice : "answered";
  const proposed = typeof meta.proposedText === "string" ? meta.proposedText : null;
  const name = typeof meta.campaignName === "string" ? meta.campaignName : "the campaign";
  const preferred = typeof meta.preferredText === "string" ? meta.preferredText : null;
  return (
    <div className="space-y-1 text-sm">
      <p className="text-muted-foreground">
        Customer {meta.changed === true ? "changed the answer: " : ""}
        <span className="text-foreground/80">{choice}</span>
        {proposed && <> ({proposed})</>} — {name}
      </p>
      {preferred && <p className="text-muted-foreground">Preferred moment: <span className="text-foreground/80">{preferred}</span></p>}
      {note && <p className="whitespace-pre-line text-foreground">{note}</p>}
    </div>
  );
}

export function ProjectLinkedBody({ meta }: { meta: Meta }) {
  const number = meta.projectNumber != null ? String(meta.projectNumber) : null;
  return (
    <span className="text-sm text-muted-foreground">
      Linked to project
      {number && <span className="text-foreground/80"> #{number}</span>}
    </span>
  );
}

export function TicketFlagChangeBody({ meta }: { meta: Meta }) {
  if (meta.flag === "descriptionInternal") {
    return (
      <span className="text-sm text-muted-foreground">
        Description set to{" "}
        <span className="text-foreground/80">{meta.to === true ? "internal" : "visible to customer"}</span>
      </span>
    );
  }
  const flag = meta.flag === "research" ? "Research" : "Call-back";
  const on = meta.to === true;
  return (
    <span className="text-sm text-muted-foreground">
      {flag} flag turned <span className="text-foreground/80">{on ? "on" : "off"}</span>
    </span>
  );
}

export function ParentLinkedBody({ meta }: { meta: Meta }) {
  const number = meta.parentNumber != null ? String(meta.parentNumber) : null;
  return (
    <span className="text-sm text-muted-foreground">
      Linked to parent ticket
      {number && <span className="text-foreground/80"> #{number}</span>}
    </span>
  );
}

export function ParentUnlinkedBody() {
  return (
    <span className="text-sm text-muted-foreground">
      Parent ticket link removed
    </span>
  );
}

export function ProjectUnlinkedBody() {
  return (
    <span className="text-sm text-muted-foreground">
      Removed from its project
    </span>
  );
}

export function ChecklistCloseBlockedBody({ meta }: { meta: Meta }) {
  const triggerName = typeof meta.triggerName === "string" ? meta.triggerName : "Trigger";
  const statusName = typeof meta.targetStatusName === "string" ? meta.targetStatusName : "a closing status";
  const lists = Array.isArray(meta.checklists)
    ? (meta.checklists as Array<{ name?: string; openRequired?: number }>)
    : [];
  return (
    <span className="text-sm text-muted-foreground">
      Trigger <span className="text-foreground/80">“{triggerName}”</span> tried to set the ticket to{" "}
      <span className="text-foreground/80">{statusName}</span> — blocked by{" "}
      {lists.length === 0 ? (
        <>an unfinished checklist</>
      ) : (
        lists.map((l, i) => (
          <span key={i}>
            {i > 0 && ", "}
            checklist <span className="text-foreground/80">“{l.name ?? "?"}”</span>
            {typeof l.openRequired === "number" && (
              <span className="text-muted-foreground/70"> ({l.openRequired} required item{l.openRequired === 1 ? "" : "s"} open)</span>
            )}
          </span>
        ))
      )}
      . Status unchanged.
    </span>
  );
}

export function IntakeFormBody({ event, meta }: { event: TicketEvent; meta: Meta }) {
  const instanceId =
    typeof meta.instanceId === "string" ? meta.instanceId : null;
  const templateName =
    typeof meta.templateName === "string" ? meta.templateName : null;
  const sentToEmail =
    typeof meta.sentToEmail === "string" ? meta.sentToEmail : null;
  const variant =
    event.eventType === "IntakeFormSubmitted"
      ? "submitted"
      : event.eventType === "IntakeFormExpired"
        ? "expired"
        : "sent";
  if (!instanceId) {
    return (
      <span className="text-sm text-muted-foreground">
        {templateName ?? "Intake form"}
      </span>
    );
  }
  return (
    <IntakeSubmissionPanel
      ticketId={event.ticketId}
      instanceId={instanceId}
      variant={variant}
      templateName={templateName}
      sentToEmail={sentToEmail}
    />
  );
}
