import { formatMonthYear } from "@/lib/dateFormat";
import { cn } from "@/lib/utils";

/// Month switcher label ("October 2026"). `ym` null renders a placeholder; `label` overrides the text (e.g. "All months").
export function MonthLabel({
  ym,
  label,
  narrow = false,
}: {
  ym: { year: number; month: number } | null;
  label?: string;
  narrow?: boolean;
}) {
  const text = label ?? (ym ? formatMonthYear(ym.year, ym.month) : "…");
  return (
    <span className={cn("px-2 text-center text-sm font-medium text-foreground", narrow ? "min-w-36" : "min-w-40")}>
      {text}
    </span>
  );
}
