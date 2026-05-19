import { useQuery } from "@tanstack/react-query";
import {
  AlertTriangle,
  BookOpen,
  Briefcase,
  Building2,
  Clock,
  FileText,
  Flag,
  GitMerge,
  HelpCircle,
  Inbox,
  Layers,
  LifeBuoy,
  Mail,
  Package,
  Settings as SettingsIcon,
  ShieldAlert,
  ShieldCheck,
  ShoppingCart,
  Tag,
  Ticket as TicketIcon,
  Users,
  Wrench,
  Zap,
  type LucideIcon,
} from "lucide-react";
import { taxonomyApi } from "@/lib/api";
import { cn } from "@/lib/utils";

// Same whitelist used by LinkedTicketTypeDialog; kept duplicate to avoid
// a circular import between the picker and this purely-presentational
// badge. Unknown icon names fall back to the generic Ticket icon.
const ICON_MAP: Record<string, LucideIcon> = {
  "life-buoy": LifeBuoy,
  "shopping-cart": ShoppingCart,
  "shield-check": ShieldCheck,
  "shield-alert": ShieldAlert,
  ticket: TicketIcon,
  package: Package,
  briefcase: Briefcase,
  inbox: Inbox,
  mail: Mail,
  zap: Zap,
  wrench: Wrench,
  clock: Clock,
  flag: Flag,
  tag: Tag,
  "alert-triangle": AlertTriangle,
  "book-open": BookOpen,
  "file-text": FileText,
  "git-merge": GitMerge,
  "help-circle": HelpCircle,
  layers: Layers,
  users: Users,
  building: Building2,
  settings: SettingsIcon,
};

function resolveIcon(name: string | undefined): LucideIcon {
  if (!name) return TicketIcon;
  return ICON_MAP[name.toLowerCase()] ?? TicketIcon;
}

type Variant = "default" | "compact";

type Props = {
  ticketTypeId: string | null | undefined;
  variant?: Variant;
  className?: string;
};

/// Renders the ticket-type pill used in headers and list rows. Variant
/// "default" shows icon + label; "compact" shrinks to icon + colour dot
/// for dense list views. Looks up the type from the cached taxonomy
/// query, so multiple instances on the same page de-dup naturally.
export function TicketTypeBadge({ ticketTypeId, variant = "default", className }: Props) {
  const typesQ = useQuery({
    queryKey: ["taxonomy", "ticket-types"],
    queryFn: () => taxonomyApi.ticketTypes.list(),
    staleTime: 5 * 60_000,
  });

  if (!ticketTypeId) return null;
  const type = typesQ.data?.find((t) => t.id === ticketTypeId);
  if (!type) return null;
  const Icon = resolveIcon(type.icon);

  if (variant === "compact") {
    return (
      <span
        className={cn(
          "inline-flex h-5 w-5 items-center justify-center rounded-md ring-1 ring-inset",
          className,
        )}
        style={{
          backgroundColor: `${type.color}1a`,
          color: type.color,
          boxShadow: `inset 0 0 0 1px ${type.color}33`,
        }}
        title={type.label}
      >
        <Icon className="h-3 w-3" />
      </span>
    );
  }

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 text-[11px] font-medium",
        className,
      )}
      style={{
        backgroundColor: `${type.color}1a`,
        color: type.color,
        borderColor: `${type.color}40`,
      }}
    >
      <Icon className="h-3 w-3" />
      {type.label}
    </span>
  );
}
