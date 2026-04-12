import DOMPurify from "dompurify";
import {
  MessageSquarePlus,
  MessageCircle,
  StickyNote,
  ArrowRightCircle,
  UserPlus,
  Flag,
  Inbox,
  Tag,
  Info,
  Mail,
} from "lucide-react";
import { cn } from "@/lib/utils";
import type { TicketEvent } from "@/lib/ticket-api";

type TicketTimelineProps = {
  events: TicketEvent[];
};

function SafeHtml({ html }: { html: string }) {
  return (
    <div
      className="prose-sm text-foreground/90 [&_a]:text-primary [&_a]:underline [&_p]:my-1 [&_ul]:pl-5 [&_ol]:pl-5"
      dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(html) }}
    />
  );
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  });
}

type EventConfig = {
  icon: React.ComponentType<{ className?: string }>;
  dotColor: string;
  label: string;
};

const EVENT_CONFIG: Record<string, EventConfig> = {
  Created: {
    icon: MessageSquarePlus,
    dotColor: "bg-purple-500",
    label: "Ticket created",
  },
  Comment: {
    icon: MessageCircle,
    dotColor: "bg-blue-500",
    label: "Comment",
  },
  Mail: {
    icon: Mail,
    dotColor: "bg-sky-500",
    label: "Email",
  },
  Note: {
    icon: StickyNote,
    dotColor: "bg-amber-500",
    label: "Internal note",
  },
  StatusChange: {
    icon: ArrowRightCircle,
    dotColor: "bg-green-500",
    label: "Status changed",
  },
  AssignmentChange: {
    icon: UserPlus,
    dotColor: "bg-indigo-400",
    label: "Assignment changed",
  },
  PriorityChange: {
    icon: Flag,
    dotColor: "bg-red-400",
    label: "Priority changed",
  },
  QueueChange: {
    icon: Inbox,
    dotColor: "bg-cyan-500",
    label: "Queue changed",
  },
  CategoryChange: {
    icon: Tag,
    dotColor: "bg-teal-500",
    label: "Category changed",
  },
  SystemNote: {
    icon: Info,
    dotColor: "bg-white/30",
    label: "System",
  },
};

function parseMetadata(json: string): Record<string, unknown> {
  try {
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return {};
  }
}

function MetaChangeText({
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
        {fieldLabel} set to <span className="text-foreground/80">{String(to)}</span>
      </span>
    );
  }
  return (
    <span className="text-sm text-muted-foreground">{fieldLabel} changed</span>
  );
}

function AssignmentText({ meta }: { meta: Record<string, unknown> }) {
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

function EventBody({ event }: { event: TicketEvent }) {
  const meta = parseMetadata(event.metadataJson);

  switch (event.eventType) {
    case "StatusChange":
      return <MetaChangeText meta={meta} fieldLabel="Status" />;

    case "AssignmentChange":
      return <AssignmentText meta={meta} />;

    case "PriorityChange":
      return <MetaChangeText meta={meta} fieldLabel="Priority" />;

    case "QueueChange":
      return <MetaChangeText meta={meta} fieldLabel="Queue" />;

    case "CategoryChange":
      return <MetaChangeText meta={meta} fieldLabel="Category" />;

    case "SystemNote":
      return (
        <span className="text-sm italic text-muted-foreground">
          {event.bodyText ?? "System event"}
        </span>
      );

    default:
      if (event.bodyHtml) {
        return <SafeHtml html={event.bodyHtml} />;
      }
      if (event.bodyText) {
        return (
          <p className="whitespace-pre-wrap text-sm text-foreground/90">
            {event.bodyText}
          </p>
        );
      }
      return null;
  }
}

function TimelineEvent({ event }: { event: TicketEvent }) {
  const config = EVENT_CONFIG[event.eventType] ?? {
    icon: Info,
    dotColor: "bg-white/30",
    label: event.eventType,
  };
  const Icon = config.icon;
  const isNote = event.eventType === "Note";
  const isSystemLike =
    event.eventType === "SystemNote" ||
    event.eventType === "StatusChange" ||
    event.eventType === "AssignmentChange" ||
    event.eventType === "PriorityChange" ||
    event.eventType === "QueueChange" ||
    event.eventType === "CategoryChange";

  return (
    <div className="relative pl-6">
      <span
        className={cn(
          "absolute -left-[9px] top-3 w-4 h-4 rounded-full border-2 border-background flex items-center justify-center",
          config.dotColor
        )}
      >
        <Icon className="w-2.5 h-2.5 text-white" />
      </span>

      {isSystemLike ? (
        <div className="flex items-center gap-2 py-1 text-sm text-muted-foreground">
          <EventBody event={event} />
          <span className="text-xs text-muted-foreground/50">
            {formatDate(event.createdUtc)}
          </span>
        </div>
      ) : (
        <div
          className={cn(
            "glass-panel p-4",
            isNote && "border-l-2 border-amber-500/50"
          )}
        >
          <div className="flex items-center justify-between gap-2 mb-2">
            <div className="flex items-center gap-2">
              <span className="text-xs font-medium text-foreground/80">
                {config.label}
              </span>
              {isNote && event.isInternal && (
                <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-amber-500/30 bg-amber-500/10 text-amber-300">
                  Internal
                </span>
              )}
            </div>
            <span className="text-xs text-muted-foreground shrink-0">
              {formatDate(event.createdUtc)}
            </span>
          </div>
          <EventBody event={event} />
        </div>
      )}
    </div>
  );
}

export function TicketTimeline({ events }: TicketTimelineProps) {
  if (events.length === 0) {
    return (
      <div className="text-sm text-muted-foreground py-4">
        No events yet.
      </div>
    );
  }

  return (
    <div className="relative border-l-2 border-white/10 space-y-4 ml-[9px]">
      {events.map((event) => (
        <TimelineEvent key={event.id} event={event} />
      ))}
    </div>
  );
}
