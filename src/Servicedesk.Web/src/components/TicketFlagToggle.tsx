import * as React from "react";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";
import { useTheme } from "@/app/ThemeProvider";

/// v0.1.17 — one Call-back / Research flag switch (ticket Status tab,
/// new-ticket drawer). The switch track takes the flag's admin-configured colour when on.
export function FlagToggleRow({
  label,
  hint,
  color,
  checked,
  onChange,
}: {
  label: string;
  hint: string;
  color: string;
  checked: boolean;
  onChange: (next: boolean) => void | Promise<void>;
}) {
  const [busy, setBusy] = React.useState(false);
  // Steaan stays flat (no gradient, no glow): a plain tint + the left bar.
  const flat = useTheme().family === "steaan";
  return (
    <label
      className={cn(
        "sd-flag-toggle flex cursor-pointer items-center justify-between gap-3 rounded-md border px-3 py-2 transition-colors",
        checked ? "border-transparent" : "border-glass bg-glass hover:bg-glass-hover",
      )}
      style={
        checked
          ? flat
            ? {
                backgroundColor: `color-mix(in srgb, ${color} 8%, white)`,
                boxShadow: `inset 3px 0 0 0 ${color}, inset 0 0 0 1px color-mix(in srgb, ${color} 28%, white)`,
              }
            : {
                backgroundImage: `linear-gradient(to right, ${color}24 0%, ${color}0d 45%, transparent 100%)`,
                boxShadow: `inset 3px 0 0 0 ${color}, inset 0 0 0 1px ${color}40`,
              }
          : undefined
      }
      title={hint}
    >
      <span className="flex min-w-0 items-center gap-2">
        <span
          aria-hidden
          className="inline-block h-2 w-2 shrink-0 rounded-full"
          style={{ backgroundColor: color, boxShadow: checked && !flat ? `0 0 8px ${color}99` : undefined }}
        />
        <span className={cn("truncate text-sm", checked ? "font-medium text-foreground" : "text-foreground/80")}>
          {label}
        </span>
      </span>
      <Switch
        checked={checked}
        disabled={busy}
        aria-label={label}
        style={checked ? { backgroundColor: color } : undefined}
        onCheckedChange={async (next) => {
          setBusy(true);
          try {
            await onChange(next);
          } catch {
            // onUpdate already surfaces the error toast.
          } finally {
            setBusy(false);
          }
        }}
      />
    </label>
  );
}
