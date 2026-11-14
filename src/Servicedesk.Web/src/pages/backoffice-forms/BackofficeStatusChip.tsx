import { cn } from "@/lib/utils";
import type { BackofficeFormItem, BackofficeFormStatus } from "@/lib/backoffice-forms-api";

const STYLE: Record<BackofficeFormStatus, string> = {
  Open: "border-amber-400/45 bg-amber-100/70 text-amber-900 dark:bg-amber-400/12 dark:text-amber-200",
  InProgress: "border-primary/30 bg-primary/10 text-primary",
  Handled: "border-glass-strong bg-glass text-muted-foreground",
};

export function backofficeStatusLabel(item: Pick<BackofficeFormItem, "status" | "claimedByName" | "handledByName">): string {
  switch (item.status) {
    case "Open": return "Open";
    case "InProgress": return item.claimedByName ? `In progress · ${item.claimedByName}` : "In progress";
    case "Handled": return item.handledByName ? `Handled · ${item.handledByName}` : "Handled";
  }
}

/// Status pill for a back-office form (list rows, dialog, ticket side panel).
export function BackofficeStatusChip({
  item,
  className,
}: {
  item: Pick<BackofficeFormItem, "status" | "claimedByName" | "handledByName">;
  className?: string;
}) {
  return (
    <span className={cn(
      "sd-bo-status inline-flex max-w-full items-center truncate rounded-md border px-2 py-0.5 text-[11px] font-medium",
      STYLE[item.status], className,
    )}>
      {backofficeStatusLabel(item)}
    </span>
  );
}
