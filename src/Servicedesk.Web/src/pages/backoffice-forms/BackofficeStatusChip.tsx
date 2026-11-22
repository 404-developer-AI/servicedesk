import { cn } from "@/lib/utils";
import type { BackofficeFormStatus } from "@/lib/backoffice-forms-api";
import { backofficeStatusLabel } from "./backofficeLabels";

/// v0.1.61 — the fixed chip vocabulary of the back-office flow. One chip per
/// state, theme tokens only:
/// - Open: amber (only where the state is not already obvious — ticket side
///   panel, overlay bar; list rows show no chip for Open)
/// - In progress · name: primary (live presence, never stored)
/// - Handled · name: neutral
export type ChipInput = {
  status: BackofficeFormStatus;
  handledByName: string | null;
  /** Live presence: who has the ticket open right now. */
  inProgressBy?: string | null;
};

const STYLE = {
  Open: "border-amber-400/45 bg-amber-100/70 text-amber-900 dark:bg-amber-400/12 dark:text-amber-200",
  InProgress: "border-primary/30 bg-primary/10 text-primary",
  Handled: "border-glass-strong bg-glass text-muted-foreground",
} as const;

/// Status pill for a back-office form (ticket side panel, overlay bar, dialog).
export function BackofficeStatusChip({ item, className }: { item: ChipInput; className?: string }) {
  const style = item.status === "Handled" ? STYLE.Handled : item.inProgressBy ? STYLE.InProgress : STYLE.Open;
  return (
    <span className={cn(
      "sd-bo-status inline-flex max-w-full items-center truncate rounded-md border px-2 py-0.5 text-[11px] font-medium",
      style, className,
    )}>
      {backofficeStatusLabel(item)}
    </span>
  );
}
