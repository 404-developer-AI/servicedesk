import * as React from "react";
import { cn } from "@/lib/utils";

// ---- v0.1.17 composer buttons ----

export type Accent = "amber" | "sky" | "violet" | "emerald";

const ACCENT_ACTIVE: Record<Accent, string> = {
  amber: "bg-amber-500/15 text-amber-300 border-amber-500/30",
  sky: "bg-sky-500/15 text-sky-300 border-sky-500/30",
  violet: "bg-violet-500/15 text-violet-300 border-violet-500/30",
  emerald: "bg-emerald-500/15 text-emerald-300 border-emerald-500/30",
};

const ACCENT_ICON: Record<Accent, string> = {
  amber: "text-amber-400/80",
  sky: "text-sky-400/80",
  violet: "text-violet-400/80",
  emerald: "text-emerald-400/80",
};

/// Collapsed-state entry point: one of the three side-by-side buttons.
export function ComposerLaunchButton({
  icon: Icon,
  label,
  hint,
  accent,
  onClick,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  hint: string;
  accent: Accent;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={hint}
      className="sd-composer-launch group flex min-w-0 items-center gap-2.5 rounded-(--radius) border border-glass bg-glass px-3 py-3 text-left text-sm text-muted-foreground transition-colors hover:border-glass-strong hover:bg-glass-hover hover:text-foreground"
    >
      <Icon className={cn("h-4 w-4 shrink-0 transition-colors", ACCENT_ICON[accent])} />
      <span className="min-w-0">
        <span className="block truncate font-medium text-foreground/85">{label}</span>
        <span className="block truncate text-[11px] text-muted-foreground/70">{hint}</span>
      </span>
    </button>
  );
}

/// Expanded-state switch between the three buttons.
export function ModeTab({
  icon: Icon,
  label,
  active,
  accent,
  onClick,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  active: boolean;
  accent: Accent;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm font-medium transition-colors",
        active
          ? ACCENT_ACTIVE[accent]
          : "border-transparent text-muted-foreground hover:bg-glass-hover hover:text-foreground",
      )}
    >
      <Icon className="h-3.5 w-3.5" />
      {label}
    </button>
  );
}

/// Sub-option of the active button (Internal note / Reply, Internal /
/// Visible to customer).
export function SubTab({
  active,
  tone,
  onClick,
  children,
}: {
  active: boolean;
  tone: Accent;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={cn(
        "rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
        active
          ? ACCENT_ACTIVE[tone]
          : "border-transparent text-muted-foreground hover:bg-glass-hover hover:text-foreground",
      )}
    >
      {children}
    </button>
  );
}
