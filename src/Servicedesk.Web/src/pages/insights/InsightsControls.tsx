import * as React from "react";
import { cn } from "@/lib/utils";

// Filter controls shared by every Insights overview (constants live in
// insightsFormat.ts).

export function Segmented({
  ariaLabel,
  value,
  options,
  onChange,
}: {
  ariaLabel: string;
  value: string;
  options: ReadonlyArray<{ value: string; label: string }>;
  onChange: (v: string) => void;
}) {
  return (
    <div role="radiogroup" aria-label={ariaLabel} className="sd-segmented inline-flex rounded-md border border-glass bg-glass p-0.5">
      {options.map((o) => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            onClick={() => onChange(o.value)}
            className={cn(
              "h-7 rounded-[5px] px-3 text-xs font-medium transition-colors",
              active
                ? "bg-background text-foreground shadow-xs"
                : "text-muted-foreground hover:text-foreground",
            )}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}

export function IconButton({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      disabled={disabled}
      className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
    >
      {children}
    </button>
  );
}

export function DateInput({
  label,
  value,
  min,
  max,
  onChange,
}: {
  label: string;
  value: string;
  min?: string;
  max?: string;
  onChange: (v: string) => void;
}) {
  return (
    <input
      type="date"
      aria-label={label}
      value={value}
      min={min}
      max={max}
      onChange={(e) => onChange(e.target.value)}
      className="h-8 rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-hidden focus:border-primary/60"
    />
  );
}
