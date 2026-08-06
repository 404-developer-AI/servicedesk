import {
  Activity,
  BarChart3,
  BookOpen,
  Building2,
  ClipboardList,
  Clock,
  Contact,
  Eye,
  FileText,
  Mail,
  MessageSquareText,
  Paperclip,
  PenLine,
  Plug,
  ScrollText,
  Shield,
  SlidersHorizontal,
  Smile,
  Ticket,
  Timer,
  UserCog,
  Users,
  Zap,
  type LucideIcon,
} from "lucide-react";

export type SettingsSection = {
  /** URL segment under /settings (e.g. "general" → /settings/general). */
  slug: string;
  label: string;
  description: string;
  icon: LucideIcon;
  /** When set, the section renders a "coming soon" stub instead of real content. */
  comingIn?: string;
  /** When true, a subtle separator is drawn below this entry in the nav rail. */
  separatorAfter?: boolean;
};

export const SETTINGS_SECTIONS: readonly SettingsSection[] = [
  {
    slug: "general",
    label: "General",
    description:
      "Branding, localization, default timezones and other app-wide knobs.",
    icon: SlidersHorizontal,
    separatorAfter: true,
  },
  {
    slug: "companies",
    label: "Companies",
    description:
      "Customer companies with their code, VAT number, contact details and per-company alert pop-ups on tickets.",
    icon: Building2,
  },
  {
    slug: "contacts",
    label: "Contacts",
    description:
      "Every contact across companies — primary/secondary/supplier links, inline details and a dedicated primary-move flow.",
    icon: Contact,
    separatorAfter: true,
  },
  {
    slug: "tickets",
    label: "Tickets",
    description:
      "Queues, statuses, priorities and categories — the taxonomies every ticket hangs off.",
    icon: Ticket,
  },
  {
    slug: "queue-access",
    label: "Queue Access",
    description: "Control which agents can access which queues.",
    icon: Shield,
  },
  {
    slug: "views",
    label: "Views",
    description:
      "Saved ticket filters — create, edit and delete named views for quick access.",
    icon: Eye,
  },
  {
    slug: "view-groups",
    label: "View Groups",
    description: "Bundle views and assign them to agents as a group.",
    icon: Users,
    separatorAfter: true,
  },
  {
    slug: "mail",
    label: "Mail",
    description:
      "Mailbox connections, polling cadence, reply parsing and auto-responders.",
    icon: Mail,
  },
  {
    slug: "integrations",
    label: "Integrations",
    description: "Microsoft 365, webhooks, outbound connectors and API tokens.",
    icon: Plug,
  },
  {
    slug: "reporting",
    label: "Reporting API",
    description:
      "Key-gated read-only endpoint external tooling can poll for ticket statistics — opened, closed and currently-open counts with ticket number + subject lists.",
    icon: BarChart3,
  },
  {
    slug: "users",
    label: "Users",
    description:
      "Agents and admins — local accounts, M365-linked accounts, role assignment, activate / deactivate.",
    icon: UserCog,
    separatorAfter: true,
  },
  {
    slug: "sla",
    label: "SLA",
    description: "Response and resolution targets, business hours, holidays, first-contact rules.",
    icon: Timer,
  },
  {
    slug: "intake-forms",
    label: "Intake Forms",
    description:
      "Reusable questionnaires agents send to customers via a public link — drag-reorder questions, bind defaults to ticket fields.",
    icon: ClipboardList,
  },
  {
    slug: "templates",
    label: "Templates",
    description:
      "Pre-canned HTML snippets agents drop into a note, reply, or outgoing mail via the :: picker. Scope each template to one or more queues. Each template can optionally link a survey that fires on send.",
    icon: FileText,
  },
  {
    slug: "signatures",
    label: "Signatures",
    description:
      "Admin-managed email signatures built from a reusable block tree. Assign per mailbox, inject agent profile variables, and sync profile photos from Entra ID.",
    icon: PenLine,
  },
  {
    slug: "surveys",
    label: "Surveys",
    description:
      "Customer satisfaction surveys with configurable rating scales, per-agent or overall scoring, and token-protected public links. Triggered via send_survey action or a compose-template hook.",
    icon: Smile,
  },
  {
    slug: "triggers",
    label: "Triggers",
    description:
      "If-this-then-that automation — auto-route by sender, auto-reply on new tickets, escalate on SLA breach.",
    icon: Zap,
  },
  {
    slug: "knowledge-base",
    label: "Knowledge Base",
    description:
      "KB-level config: active toggle, default locale, supported locales and the section tree that articles live in.",
    icon: BookOpen,
  },
  {
    slug: "timesheet",
    label: "Timesheet",
    description:
      "Global defaults for the registration grid (start time of a new day, daily and weekly targets, work-days set), the task catalogue agents pick from, and the reply-template HTML fragments. Timing defaults can be overridden per user from Users → row action → Timesheet overrides.",
    icon: Clock,
  },
  {
    slug: "feedback",
    label: "Employee Feedback",
    description:
      "Work-point type catalogue — the categories agents and managers can assign to feedback entries. Manage names, colors and sort order; deactivate types that are in use.",
    icon: MessageSquareText,
  },
  {
    slug: "mail-diagnostics",
    label: "Mail diagnostics",
    description:
      "Inspect attachment-pipeline state for an ingested mail — row state, worker-job state, blob presence.",
    icon: Paperclip,
  },
  {
    slug: "audit",
    label: "Audit log",
    description:
      "Append-only HMAC-chained record of security events — rate limits, CSP violations, setting changes.",
    icon: ScrollText,
  },
  {
    slug: "health",
    label: "Health",
    description:
      "Live status of background subsystems — mail polling, Graph credentials, storage. Retry actions and troubleshooting.",
    icon: Activity,
  },
];

export const DEFAULT_SETTINGS_SECTION = SETTINGS_SECTIONS[0]!.slug;

export function findSettingsSection(slug: string): SettingsSection | undefined {
  return SETTINGS_SECTIONS.find((s) => s.slug === slug);
}
