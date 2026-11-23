import type { ContactCompanyRole } from "@/lib/ticket-api";

export const LINK_ROLE_BADGE: Record<ContactCompanyRole, string> = {
  primary: "border-purple-400/50 bg-purple-500/20 text-purple-100",
  secondary: "border-sky-400/30 bg-sky-500/15 text-sky-200",
  supplier: "border-amber-400/30 bg-amber-500/15 text-amber-200",
};
