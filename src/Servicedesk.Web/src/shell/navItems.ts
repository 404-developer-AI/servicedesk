import {
  LayoutDashboard,
  ListChecks,
  Inbox,
  BookOpen,
  Settings,
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
    label: "Views",
    to: "/views",
    icon: ListChecks,
    roles: ["Agent", "Admin"],
    comingIn: "v0.0.5",
    description: "User-defined saved ticket views with filters, sorting and shared queries.",
    section: "main",
  },
  {
    label: "Open Tickets",
    to: "/tickets",
    icon: Inbox,
    roles: ["Agent", "Admin"],
    comingIn: "v0.0.5",
    description: "The ticket queue — fast list, virtualized, search and bulk actions.",
    section: "main",
  },
  {
    label: "Knowledge Base",
    to: "/kb",
    icon: BookOpen,
    roles: ["Customer", "Agent", "Admin"],
    comingIn: "v0.0.11",
    description: "Articles and runbooks with full-text search and inline suggestions.",
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
