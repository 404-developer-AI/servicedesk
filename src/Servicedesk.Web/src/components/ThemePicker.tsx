import { useEffect, useRef } from "react";
import { Check, Moon, Sun } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  themeFamily,
  themeMode,
  type ThemeFamily,
  type ThemeMode,
  type UiTheme,
} from "@/lib/theme";

type ThemePickerProps = {
  value: UiTheme;
  onChange: (next: UiTheme) => void;
  disabled?: boolean;
  /** Accessible name for the family radiogroup. */
  label?: string;
};

const FAMILIES: {
  id: ThemeFamily;
  name: string;
  blurb: string;
}[] = [
  {
    id: "steaan",
    name: "Steaan",
    blurb: "Flat, warm-neutral surfaces with a teal accent. Light only.",
  },
  {
    id: "nebula",
    name: "Nebula",
    blurb: "Glass surfaces on a purple-blue gradient. Light or dark.",
  },
];

/**
 * Two-level theme picker shared by Profile → Appearance (the user's own
 * preference) and Settings → General (the instance default). Level one
 * picks the family — Steaan or Nebula — as preview cards; level two (only
 * shown for Nebula) picks Light/Dark. Steaan is light-only by design, so
 * the mode toggle disappears while it is selected rather than being
 * disabled: there is no dark Steaan to tease.
 *
 * Identifiers map straight onto the persisted values: Steaan → `steaan`,
 * Nebula → `light` / `dark`.
 */
export function ThemePicker({
  value,
  onChange,
  disabled,
  label = "Theme",
}: ThemePickerProps) {
  const family = themeFamily(value);
  const mode = themeMode(value);
  // Remember the last Nebula mode so Steaan → Nebula lands where the user
  // left it instead of always resetting to Light.
  const lastNebulaMode = useRef<ThemeMode>(family === "nebula" ? mode : "light");
  useEffect(() => {
    if (family === "nebula") lastNebulaMode.current = mode;
  }, [family, mode]);

  const pickFamily = (next: ThemeFamily) => {
    if (disabled) return;
    if (next === "steaan") onChange("steaan");
    else onChange(lastNebulaMode.current);
  };

  return (
    <div className="space-y-3">
      <div
        role="radiogroup"
        aria-label={label}
        className="grid grid-cols-1 gap-3 sm:grid-cols-2"
      >
        {FAMILIES.map((f) => {
          const selected = family === f.id;
          return (
            <button
              key={f.id}
              type="button"
              role="radio"
              aria-checked={selected}
              disabled={disabled}
              onClick={() => pickFamily(f.id)}
              className={cn(
                "group relative flex items-start gap-3 rounded-xl border p-3 text-left transition-all",
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
                "disabled:cursor-not-allowed disabled:opacity-50",
                selected
                  ? "border-primary bg-primary/[0.06] shadow-[inset_0_0_0_1px_hsl(var(--primary))]"
                  : "border-glass bg-glass hover:border-glass-strong hover:bg-glass-hover",
              )}
            >
              <ThemeSwatch family={f.id} mode={f.id === "nebula" ? mode : "light"} />
              <div className="min-w-0 flex-1 pt-0.5">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-semibold text-foreground">{f.name}</span>
                  {selected && (
                    <span className="inline-flex h-4 w-4 items-center justify-center rounded-full bg-primary text-primary-foreground">
                      <Check className="h-3 w-3" strokeWidth={3} />
                    </span>
                  )}
                </div>
                <p className="mt-0.5 text-[11px] leading-snug text-muted-foreground">{f.blurb}</p>
              </div>
            </button>
          );
        })}
      </div>

      {family === "nebula" && (
        <div className="flex items-center gap-3">
          <span className="text-[11px] uppercase tracking-[0.18em] text-muted-foreground">
            Mode
          </span>
          <div
            role="radiogroup"
            aria-label={`${label} mode`}
            className="inline-flex rounded-lg border border-glass bg-glass p-1"
          >
            {(["light", "dark"] as const).map((m) => (
              <button
                key={m}
                type="button"
                role="radio"
                aria-checked={mode === m}
                disabled={disabled}
                onClick={() => onChange(m)}
                className={cn(
                  "flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium transition-colors",
                  "disabled:cursor-not-allowed disabled:opacity-50",
                  mode === m
                    ? "bg-primary text-primary-foreground shadow-sm"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {m === "light" ? <Sun className="h-3.5 w-3.5" /> : <Moon className="h-3.5 w-3.5" />}
                {m === "light" ? "Light" : "Dark"}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Miniature "app window" per family — sidebar strip, a card and an accent
 * pill — drawn with the target theme's own colours so the card previews
 * the look instead of describing it. Purely decorative.
 */
function ThemeSwatch({ family, mode }: { family: ThemeFamily; mode: ThemeMode }) {
  if (family === "steaan") {
    return (
      <div
        aria-hidden
        className="flex h-14 w-20 shrink-0 overflow-hidden rounded-md border"
        style={{ backgroundColor: "#FAFAF9", borderColor: "#E7E5E4" }}
      >
        <div className="flex w-6 flex-col gap-1 border-r p-1" style={{ backgroundColor: "#FFFFFF", borderColor: "#E7E5E4" }}>
          <div className="h-1.5 w-1.5 rounded-sm" style={{ backgroundColor: "#0F766E" }} />
          <div className="mt-1 h-1 rounded-sm" style={{ backgroundColor: "#F0FDFA", boxShadow: "inset 2px 0 0 #0F766E" }} />
          <div className="h-1 rounded-sm" style={{ backgroundColor: "#E7E5E4" }} />
          <div className="h-1 rounded-sm" style={{ backgroundColor: "#E7E5E4" }} />
        </div>
        <div className="flex flex-1 flex-col gap-1 p-1.5">
          <div className="h-1.5 w-8 rounded-sm" style={{ backgroundColor: "#1C1917", opacity: 0.8 }} />
          <div
            className="flex-1 rounded-[3px] border"
            style={{ backgroundColor: "#FFFFFF", borderColor: "#E7E5E4", boxShadow: "0 1px 2px rgba(28,25,23,0.05)" }}
          />
          <div className="h-1.5 w-5 rounded-full" style={{ backgroundColor: "#0F766E" }} />
        </div>
      </div>
    );
  }
  const dark = mode === "dark";
  const canvas = dark
    ? "radial-gradient(at 20% 0%, hsl(265 89% 30% / 0.8) 0, transparent 55%), radial-gradient(at 80% 100%, hsl(220 89% 35% / 0.7) 0, transparent 55%), hsl(240 12% 4%)"
    : "radial-gradient(at 20% 0%, hsl(265 85% 70% / 0.35) 0, transparent 55%), radial-gradient(at 80% 100%, hsl(220 85% 70% / 0.35) 0, transparent 55%), hsl(240 25% 98%)";
  const glass = dark ? "hsl(0 0% 100% / 0.08)" : "hsl(240 30% 30% / 0.07)";
  const glassBorder = dark ? "hsl(0 0% 100% / 0.16)" : "hsl(240 25% 25% / 0.14)";
  const ink = dark ? "hsl(0 0% 98%)" : "hsl(240 18% 14%)";
  return (
    <div
      aria-hidden
      className="flex h-14 w-20 shrink-0 gap-1 overflow-hidden rounded-md border p-1"
      style={{ background: canvas, borderColor: glassBorder }}
    >
      <div
        className="flex w-5 flex-col gap-1 rounded-[3px] border p-1"
        style={{ backgroundColor: glass, borderColor: glassBorder }}
      >
        <div className="h-1.5 w-1.5 rounded-sm" style={{ background: "linear-gradient(135deg, hsl(265 89% 70%), hsl(220 89% 65%))" }} />
        <div className="mt-1 h-1 rounded-sm" style={{ backgroundColor: glassBorder }} />
        <div className="h-1 rounded-sm" style={{ backgroundColor: glassBorder }} />
      </div>
      <div className="flex flex-1 flex-col gap-1">
        <div className="h-1.5 w-7 rounded-sm" style={{ backgroundColor: ink, opacity: 0.75 }} />
        <div className="flex-1 rounded-[3px] border" style={{ backgroundColor: glass, borderColor: glassBorder }} />
        <div className="h-1.5 w-5 rounded-full" style={{ background: "linear-gradient(90deg, hsl(265 89% 70%), hsl(220 89% 65%))" }} />
      </div>
    </div>
  );
}
