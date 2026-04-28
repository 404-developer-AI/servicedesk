import { cn } from "@/lib/utils";

export type IntegrationStatus = "online" | "error" | "not-configured";

const STATUS_STYLES: Record<
  IntegrationStatus,
  { pill: string; dot: string; label: string }
> = {
  online: {
    pill: "border-emerald-400/30 bg-emerald-500/10 text-emerald-300",
    dot: "bg-emerald-400",
    label: "Online",
  },
  error: {
    pill: "border-rose-400/40 bg-rose-500/10 text-rose-300",
    dot: "bg-rose-400",
    label: "Error",
  },
  "not-configured": {
    pill: "border-white/15 bg-white/[0.06] text-muted-foreground",
    dot: "bg-white/40",
    label: "Not configured",
  },
};

export type IntegrationTileProps = {
  /** Display name — used as accessible label only; never rendered visibly. */
  name: string;
  /** Logo image src. */
  logo: string;
  /**
   * "wordmark" (default) suits wider logos that already contain the brand name.
   * "icon" suits tall/square brand marks and renders the artwork a touch larger.
   */
  variant?: "wordmark" | "icon";
  status: IntegrationStatus;
  onClick?: () => void;
  className?: string;
};

export function IntegrationTile({
  name,
  logo,
  variant = "wordmark",
  status,
  onClick,
  className,
}: IntegrationTileProps) {
  const style = STATUS_STYLES[status];
  const interactive = typeof onClick === "function";

  const baseClasses = cn(
    "glass-card group relative flex aspect-square items-center justify-center overflow-hidden px-5 pb-12 pt-5",
    interactive &&
      "cursor-pointer transition duration-200 hover:-translate-y-0.5 hover:border-white/15 hover:bg-white/[0.07]",
    className,
  );

  const inner = (
    <>
      <img
        src={logo}
        alt=""
        aria-hidden="true"
        draggable={false}
        className={cn(
          "w-auto select-none object-contain",
          variant === "icon" ? "max-h-16 max-w-[60%]" : "max-h-12 max-w-[75%]",
        )}
      />
      <div className="pointer-events-none absolute inset-x-0 bottom-3 flex justify-center">
        <span
          className={cn(
            "pointer-events-auto inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11px] font-normal",
            style.pill,
          )}
        >
          <span className={cn("h-1.5 w-1.5 rounded-full", style.dot)} />
          {style.label}
        </span>
      </div>
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 flex items-center justify-center bg-background/70 px-4 text-center opacity-0 backdrop-blur-sm transition-opacity duration-200 group-hover:opacity-100"
      >
        <span className="text-sm font-semibold text-foreground">{name}</span>
      </div>
      <span className="sr-only">{name}</span>
    </>
  );

  if (interactive) {
    return (
      <button type="button" onClick={onClick} aria-label={name} className={baseClasses}>
        {inner}
      </button>
    );
  }

  return (
    <div role="group" aria-label={name} className={baseClasses}>
      {inner}
    </div>
  );
}
