import {
  ChevronRight,
  LifeBuoy,
  ShieldCheck,
  ShoppingCart,
  Ticket as TicketIcon,
  Mail,
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
  Loader2,
  Package,
  Settings as SettingsIcon,
  ShieldAlert,
  Tag,
  Users,
  Wrench,
  Zap,
  type LucideIcon,
} from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";
import type { LinkedTicketPresetSummary } from "@/lib/ticket-api";

type Props = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  presets: LinkedTicketPresetSummary[] | undefined;
  loading: boolean;
  onSelect: (preset: LinkedTicketPresetSummary) => void;
};

// Small whitelist that maps the admin-chosen icon string on a ticket-type
// to a lucide component. Keeps the bundle in control — admins type a
// known token rather than letting the FE import everything dynamically.
// Unknown strings fall back to the generic Ticket icon.
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

function resolveIcon(name: string): LucideIcon {
  return ICON_MAP[name?.toLowerCase()] ?? TicketIcon;
}

export function LinkedTicketTypeDialog({
  open,
  onOpenChange,
  presets,
  loading,
  onSelect,
}: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md border-glass-strong bg-background/85 backdrop-blur-xl">
        <DialogHeader>
          <DialogTitle className="font-display">Create linked ticket</DialogTitle>
          <DialogDescription>Select the type of ticket to link</DialogDescription>
        </DialogHeader>
        <div className="mt-2 flex flex-col gap-2">
          {loading && (
            <div className="flex items-center justify-center gap-2 py-8 text-sm text-muted-foreground">
              <Loader2 className="h-4 w-4 animate-spin" />
              Loading presets…
            </div>
          )}

          {!loading && presets?.length === 0 && (
            <div className="rounded-lg border border-glass-strong bg-glass px-4 py-6 text-center text-sm text-muted-foreground">
              No linked-ticket presets configured yet.
              <p className="mt-1 text-xs text-muted-foreground/80">
                An admin can add a manual trigger under Settings → Triggers
                to enable a button here.
              </p>
            </div>
          )}

          {!loading && presets?.map((preset) => {
            const Icon = resolveIcon(preset.ticketTypeIcon);
            return (
              <button
                key={preset.triggerId}
                type="button"
                onClick={() => onSelect(preset)}
                className={cn(
                  "group relative flex items-start gap-3 rounded-lg border border-glass-strong bg-glass px-4 py-3 text-left transition",
                  "hover:border-primary/40 hover:bg-primary/[0.06] focus:outline-none focus-visible:ring-2 focus-visible:ring-primary/40",
                )}
              >
                <div
                  className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-md ring-1 ring-inset"
                  style={{
                    backgroundColor: `${preset.ticketTypeColor}1a`,
                    color: preset.ticketTypeColor,
                    // 25% alpha ring via hex+aa suffix style fallback
                    boxShadow: `inset 0 0 0 1px ${preset.ticketTypeColor}33`,
                  }}
                >
                  <Icon className="h-5 w-5" />
                </div>
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium text-foreground">
                      Create linked {preset.ticketTypeLabel}
                    </span>
                  </div>
                  <p className="mt-0.5 text-xs text-muted-foreground">
                    {preset.ticketTypeDescription || preset.name}
                  </p>
                </div>
                <ChevronRight className="mt-2 h-4 w-4 shrink-0 text-muted-foreground/40 transition group-hover:translate-x-0.5 group-hover:text-primary" />
              </button>
            );
          })}
        </div>
      </DialogContent>
    </Dialog>
  );
}
