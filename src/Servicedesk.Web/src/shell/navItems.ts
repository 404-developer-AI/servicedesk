import {
  LayoutDashboard,
  Inbox,
  BookOpen,
  Clock,
  Settings,
  Timer,
  Activity,
  Server,
  ShoppingCart,
  type LucideIcon,
} from "lucide-react";
import type { Role } from "@/lib/roles";

export type NavSection = "main" | "footer";

export type NavItem = {
  label: string;
  to: string;
  icon: LucideIcon;
  roles: readonly Role[];
  comingIn: string;
  description: string;
  section: NavSection;
};

export const NAV_ITEMS: readonly NavItem[] = [
  {
    label: "Dashboard",
    to: "/",
    icon: LayoutDashboard,
    roles: ["Customer", "Agent", "Admin"],
    comingIn: "v0.0.13",
    description: "Live metrics, SLA health, ticket volume and team load at a glance.",
    section: "main",
  },
{
    label: "Open Tickets",
    to: "/tickets",
    icon: Inbox,
    roles: ["Agent", "Admin"],
    comingIn: "",
    description: "The ticket queue — fast list, virtualized, search and bulk actions.",
    section: "main",
  },
  {
    label: "SLA log",
    to: "/sla-log",
    icon: Timer,
    roles: ["Admin"],
    comingIn: "",
    description: "Per-ticket timing — first-response and resolution with filters and date picker.",
    section: "main",
  },
  {
    label: "Knowledge Base",
    to: "/kb",
    icon: BookOpen,
    roles: ["Agent", "Admin"],
    comingIn: "",
    description: "Internal articles and runbooks with full-text search.",
    section: "main",
  },
  // v0.0.35 — per-user feature flag (`timesheet_enabled` /
  // `timesheet_manager`). The role gate stays on Agent+Admin; the actual
  // visibility filter on the flag lives in Sidebar.tsx so a user without
  // either flag never sees the item even though their role qualifies.
  {
    label: "Timesheet",
    to: "/timesheet",
    icon: Clock,
    roles: ["Agent", "Admin"],
    comingIn: "",
    description: "Daily time registration — own entries, ticket-linked, with manager overview for opted-in users.",
    section: "main",
  },
  // v0.0.42 — Activity feed. Role gate keeps customers out; the per-user
  // `activity_feed_enabled` flag visibility filter lives in Sidebar.tsx
  // so a user without the flag never sees this entry.
  {
    label: "Activity",
    to: "/activity",
    icon: Activity,
    roles: ["Agent", "Admin"],
    comingIn: "",
    description: "Append-only feed of every agent + admin action across the app, with filters and search.",
    section: "main",
  },
  // v0.0.52 — Assets. Mirrored from Tactical RMM (one TRMM install per
  // Servicedesk install). Role gate is Agent+Admin; the per-user
  // `assets_enabled` flag is checked in Sidebar.tsx, and the backend
  // /api/assets endpoints carry the matching RequireAgent policy.
  {
    label: "Assets",
    to: "/assets",
    icon: Server,
    roles: ["Agent", "Admin"],
    comingIn: "",
    description: "Servers and workstations mirrored from Tactical RMM, with filters for Windows build and online state.",
    section: "main",
  },
  // v0.0.59 — Orders (bestellingen). Mirrored from the Adsolut ERP
  // OrderInfos endpoint. Role gate is Agent+Admin; the per-user
  // `adsolut_orders_enabled` flag is checked in Sidebar.tsx, and the backend
  // /api/orders endpoints carry the matching RequireAgent policy. Sits
  // directly under Assets in the nav.
  {
    label: "Orders",
    to: "/orders",
    icon: ShoppingCart,
    roles: ["Agent", "Admin"],
    comingIn: "",
    description: "Adsolut orders (bestellingen) mirrored from the ERP — overview with per-order detail lines.",
    section: "main",
  },
  // Profile is reachable from the header avatar dropdown (top-right) and via
  // direct URL. Intentionally not in NAV_ITEMS so the primary nav and command
  // palette stay focused on workflow pages.
  {
    label: "Settings",
    to: "/settings",
    icon: Settings,
    roles: ["Admin"],
    comingIn: "v0.0.3",
    description: "App-wide configuration — grouped, searchable, audit-logged.",
    section: "footer",
  },
] as const;

export function visibleNavItems(role: Role, section: NavSection = "main"): readonly NavItem[] {
  return NAV_ITEMS.filter((item) => item.section === section && item.roles.includes(role));
}

/// All sections combined, for surfaces like the command palette that want
/// every jump target regardless of where it's pinned in the sidebar.
export function allVisibleNavItems(role: Role): readonly NavItem[] {
  return NAV_ITEMS.filter((item) => item.roles.includes(role));
}

export function findNavItem(path: string): NavItem | undefined {
  return NAV_ITEMS.find((item) => item.to === path);
}
