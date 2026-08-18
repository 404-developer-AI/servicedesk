import { cn } from "@/lib/utils";

/// Small SVG progress ring used by the checklist bar, header chip and panel.
/// Tone follows completion: amber while work remains, emerald when done,
/// muted when there is nothing to do.
export function ProgressRing({
  done,
  total,
  size = 28,
  stroke = 3,
  className,
  children,
}: {
  done: number;
  total: number;
  size?: number;
  stroke?: number;
  className?: string;
  children?: React.ReactNode;
}) {
  const ratio = total > 0 ? Math.min(1, Math.max(0, done / total)) : 0;
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const complete = total > 0 && done >= total;
  const tone = total === 0 ? "text-muted-foreground/40" : complete ? "text-emerald-400" : "text-amber-400";
  return (
    <span
      className={cn("relative inline-flex shrink-0 items-center justify-center", className)}
      style={{ width: size, height: size }}
      aria-hidden
    >
      <svg width={size} height={size} className="-rotate-90">
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          strokeWidth={stroke}
          className="stroke-current text-foreground/10"
        />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={c}
          strokeDashoffset={c * (1 - ratio)}
          className={cn("stroke-current transition-[stroke-dashoffset] duration-500 ease-out", tone)}
        />
      </svg>
      {children && (
        <span className="absolute inset-0 flex items-center justify-center">{children}</span>
      )}
    </span>
  );
}
