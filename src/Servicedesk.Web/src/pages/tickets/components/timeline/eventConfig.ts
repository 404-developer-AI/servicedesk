import type { ComponentType } from "react";
import {
  MessageSquarePlus,
  MessageCircle,
  StickyNote,
  ArrowRightCircle,
  UserPlus,
  Flag,
  Phone,
  Inbox,
  Tag,
  Info,
  Mail,
  MailOpen,
  Send,
  Building2,
  UserCog,
  ClipboardList,
  ClipboardCheck,
  ClipboardX,
  AlertTriangle,
  TimerReset,
  Ban,
  ListChecks,
  FolderKanban,
  Link2,
  CalendarClock,
  Megaphone,
} from "lucide-react";
import type { TicketEvent } from "@/lib/ticket-api";

type EventConfig = {
  icon: ComponentType<{ className?: string }>;
  dotColor: string;
  label: string;
};

export const EVENT_CONFIG: Record<string, EventConfig> = {
  Created: {
    icon: MessageSquarePlus,
    dotColor: "bg-purple-500",
    label: "Ticket created",
  },
  Comment: {
    icon: MessageCircle,
    dotColor: "bg-amber-500",
    label: "Reply",
  },
  // v0.1.17 — a phone call logged from the Call composer (internal by
  // default, optionally customer-visible).
  Call: {
    icon: Phone,
    dotColor: "bg-violet-500",
    label: "Phone call",
  },
  Mail: {
    icon: Mail,
    dotColor: "bg-sky-500",
    label: "Email",
  },
  MailReceived: {
    icon: MailOpen,
    dotColor: "bg-sky-400",
    label: "Mail received",
  },
  // v0.1.0 — a customer wrote this from the portal (new-ticket
  // description or reply). Customer-visible article like an inbound mail.
  PortalMessage: {
    icon: MessageCircle,
    dotColor: "bg-teal-500",
    label: "Portal message",
  },
  MailSent: {
    icon: Send,
    dotColor: "bg-sky-500",
    label: "Mail sent",
  },
  Note: {
    icon: StickyNote,
    dotColor: "bg-blue-500",
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
  CompanyAssignment: {
    icon: Building2,
    dotColor: "bg-purple-400",
    label: "Company assigned",
  },
  RequesterChange: {
    icon: UserCog,
    dotColor: "bg-fuchsia-400",
    label: "Requester changed",
  },
  SystemNote: {
    icon: Info,
    dotColor: "bg-glass-strong",
    label: "System",
  },
  IntakeFormSent: {
    icon: ClipboardList,
    dotColor: "bg-emerald-500",
    label: "Intake form sent",
  },
  IntakeFormSubmitted: {
    icon: ClipboardCheck,
    dotColor: "bg-emerald-400",
    label: "Intake form submitted",
  },
  IntakeFormExpired: {
    icon: ClipboardX,
    dotColor: "bg-orange-400",
    label: "Intake form expired",
  },
  TimeLimitAlertDismissed: {
    icon: AlertTriangle,
    dotColor: "bg-amber-500",
    label: "Hour-limit warning dismissed",
  },
  TimeLimitExtended: {
    icon: TimerReset,
    dotColor: "bg-violet-500",
    label: "Hour limit raised",
  },
  TimeLimitTrackingDisabled: {
    icon: Ban,
    dotColor: "bg-amber-500",
    label: "Hour tracking disabled",
  },
  // v0.0.103 — ticket checklists.
  ChecklistAttached: { icon: ListChecks, dotColor: "bg-violet-500", label: "Checklist added" },
  ChecklistDetached: { icon: ListChecks, dotColor: "bg-glass-strong", label: "Checklist removed" },
  ChecklistCompleted: { icon: ListChecks, dotColor: "bg-emerald-500", label: "Checklist completed" },
  ChecklistReopened: { icon: ListChecks, dotColor: "bg-amber-500", label: "Checklist reopened" },
  ChecklistItemChanged: { icon: ListChecks, dotColor: "bg-violet-400", label: "Checklist item changed" },
  ChecklistCloseBlocked: { icon: ListChecks, dotColor: "bg-amber-500", label: "Trigger blocked by checklist" },
  // v0.0.105 — project tickets.
  ProjectConverted: { icon: FolderKanban, dotColor: "bg-sky-500", label: "Converted to project" },
  ProjectReverted: { icon: FolderKanban, dotColor: "bg-glass-strong", label: "Project flag removed" },
  ProjectLinked: { icon: FolderKanban, dotColor: "bg-sky-500", label: "Linked to project" },
  ProjectUnlinked: { icon: FolderKanban, dotColor: "bg-glass-strong", label: "Removed from project" },
  // Parent/child links (LinkParentDialog).
  ParentLinked: { icon: Link2, dotColor: "bg-sky-500", label: "Linked to parent ticket" },
  ParentUnlinked: { icon: Link2, dotColor: "bg-glass-strong", label: "Parent link removed" },
  // v0.1.17 — Call-back / Research flag switched on or off.
  TicketFlagChange: { icon: Flag, dotColor: "bg-emerald-500", label: "Ticket flag changed" },
  // v0.1.44 — an agent changed "Pending till" (side panel or bulk).
  PendingTillChange: { icon: CalendarClock, dotColor: "bg-amber-500", label: "Pending till changed" },
  // v0.1.67 — a campaign round finished sending (on the linked project only).
  CampaignSent: { icon: Megaphone, dotColor: "bg-sky-500", label: "Campaign sent" },
  // v0.1.67 — the customer answered a campaign through its answer link.
  CampaignAnswer: { icon: Megaphone, dotColor: "bg-emerald-500", label: "Campaign answer" },
};

/// Event types that are system/audit noise rather than real
/// communication. Hidden from the timeline by default; an agent reveals
/// them for an audit pass via the side-panel toggle. Exported so the
/// detail page can both filter the feed and count what's hidden.
const SYSTEM_EVENT_TYPES = new Set<string>([
  "SystemNote",
  "StatusChange",
  "AssignmentChange",
  "PriorityChange",
  "QueueChange",
  "CategoryChange",
  "CompanyAssignment",
  "RequesterChange",
  "Created",
  "TimeLimitAlertDismissed",
  "TimeLimitExtended",
  "TimeLimitTrackingDisabled",
  "ChecklistAttached",
  "ChecklistDetached",
  "ChecklistCompleted",
  "ChecklistReopened",
  "ChecklistItemChanged",
  "ChecklistCloseBlocked",
  "ProjectConverted",
  "ProjectReverted",
  "ProjectLinked",
  "ProjectUnlinked",
  "ParentLinked",
  "ParentUnlinked",
  "TicketFlagChange",
  "PendingTillChange",
]);

export function isSystemEvent(event: TicketEvent): boolean {
  return SYSTEM_EVENT_TYPES.has(event.eventType);
}

export function isSystemEventType(eventType: string): boolean {
  return SYSTEM_EVENT_TYPES.has(eventType);
}
