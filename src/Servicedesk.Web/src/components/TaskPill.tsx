import { cn } from "@/lib/utils";

/** Task-name badge for timesheet rows; absence tasks get the amber treatment. */
export function TaskPill({
  name,
  isAbsence,
  truncate = false,
}: {
  name: string;
  isAbsence: boolean;
  /** Clamp long names to the container width and expose the full name as a tooltip. */
  truncate?: boolean;
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center rounded-full border px-2 py-0.5 text-[11px] font-medium",
        truncate && "max-w-full truncate",
        isAbsence
          ? "border-amber-400/30 bg-amber-400/10 text-amber-200"
          : "border-glass bg-glass text-foreground/80",
      )}
      title={truncate ? name : undefined}
    >
      {name}
    </span>
  );
}
