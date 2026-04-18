import {
  Activity,
  Building2,
  Contact,
  Eye,
  Mail,
  Paperclip,
  Plug,
  ScrollText,
  Shield,
  SlidersHorizontal,
  Ticket,
  Timer,
  Users,
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
};

export const SETTINGS_SECTIONS: readonly SettingsSection[] = [
  {
    slug: "general",
    label: "General",
    description:
      "Branding, localization, default timezones and other app-wide knobs.",
    icon: SlidersHorizontal,
  },
  {
    slug: "tickets",
    label: "Tickets",
    description:
      "Queues, statuses, priorities and categories — the taxonomies every ticket hangs off.",
    icon: Ticket,
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
  },
  {
    slug: "views",
    label: "Views",
    description:
      "Saved ticket filters — create, edit and delete named views for quick access.",
    icon: Eye,
  },
  {
    slug: "queue-access",
    label: "Queue Access",
    description: "Control which agents can access which queues.",
    icon: Shield,
  },
  {
    slug: "view-groups",
    label: "View Groups",
    description: "Bundle views and assign them to agents as a group.",
    icon: Users,
  },
  {
    slug: "mail",
    label: "Mail",
    description:
      "Mailbox connections, polling cadence, reply parsing and auto-responders.",
    icon: Mail,
  },
  {
    slug: "sla",
    label: "SLA",
    description: "Response and resolution targets, business hours, holidays, first-contact rules.",
    icon: Timer,
  },
  {
    slug: "integrations",
    label: "Integrations",
    description: "Microsoft 365, webhooks, outbound connectors and API tokens.",
    icon: Plug,
  },
  {
    slug: "mail-diagnostics",
    label: "Mail diagnostics",
    description:
      "Inspect attachment-pipeline state for an ingested mail — row state, worker-job state, blob presence.",
    icon: Paperclip,
  },
  {
    slug: "health",
    label: "Health",
    description:
      "Live status of background subsystems — mail polling, Graph credentials, storage. Retry actions and troubleshooting.",
    icon: Activity,
  },
  {
    slug: "audit",
    label: "Audit log",
    description:
      "Append-only HMAC-chained record of security events — rate limits, CSP violations, setting changes.",
    icon: ScrollText,
  },
];

export const DEFAULT_SETTINGS_SECTION = SETTINGS_SECTIONS[0]!.slug;

export function findSettingsSection(slug: string): SettingsSection | undefined {
  return SETTINGS_SECTIONS.find((s) => s.slug === slug);
}
