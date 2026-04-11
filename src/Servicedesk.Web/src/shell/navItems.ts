import {
  LayoutDashboard,
  ListChecks,
  Inbox,
  BookOpen,
  User,
  Settings,
  type LucideIcon,
} from "lucide-react";
import type { Role } from "@/lib/roles";

export type NavItem = {
  label: string;
  to: string;
  icon: LucideIcon;
  roles: readonly Role[];
  comingIn: string;
  description: string;
};

export const NAV_ITEMS: readonly NavItem[] = [
  {
    label: "Dashboard",
    to: "/",
    icon: LayoutDashboard,
    roles: ["Customer", "Agent", "Admin"],
    comingIn: "v0.0.13",
    description: "Live metrics, SLA health, ticket volume and team load at a glance.",
  },
  {
    label: "Views",
    to: "/views",
    icon: ListChecks,
    roles: ["Agent", "Admin"],
    comingIn: "v0.0.5",
    description: "User-defined saved ticket views with filters, sorting and shared queries.",
  },
  {
    label: "Open Tickets",
    to: "/tickets",
    icon: Inbox,
    roles: ["Agent", "Admin"],
    comingIn: "v0.0.5",
    description: "The ticket queue — fast list, virtualized, search and bulk actions.",
  },
  {
    label: "Knowledge Base",
    to: "/kb",
    icon: BookOpen,
    roles: ["Customer", "Agent", "Admin"],
    comingIn: "v0.0.11",
    description: "Articles and runbooks with full-text search and inline suggestions.",
  },
  {
    label: "Profile",
    to: "/profile",
    icon: User,
    roles: ["Customer", "Agent", "Admin"],
    comingIn: "v0.0.4",
    description: "Your account, password, 2FA and personal preferences.",
  },
  {
    label: "Settings",
    to: "/settings",
    icon: Settings,
    roles: ["Admin"],
    comingIn: "v0.0.3",
    description: "App-wide configuration — grouped, searchable, audit-logged.",
  },
] as const;

export function visibleNavItems(role: Role): readonly NavItem[] {
  return NAV_ITEMS.filter((item) => item.roles.includes(role));
}

export function findNavItem(path: string): NavItem | undefined {
  return NAV_ITEMS.find((item) => item.to === path);
}
