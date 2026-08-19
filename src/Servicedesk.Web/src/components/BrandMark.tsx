import { Ticket } from "lucide-react";
import { useTheme } from "@/app/ThemeProvider";
import { cn } from "@/lib/utils";
import ticksyMark from "@/assets/brand/ticksy.svg";
import ticksyWordmarkDark from "@/assets/brand/ticksy-dark.svg";
import ticksyWordmarkLight from "@/assets/brand/ticksy-light.svg";

/**
 * Brand mark per theme family. Nebula keeps the Ticksy artwork (the purple
 * mark in the sidebar, the light/dark wordmark on the login card); Steaan
 * uses the mark from the Steaan mockups — a teal rounded square with a
 * ticket glyph — so the house style is not interrupted by a purple logo.
 *
 * `size` is the square's edge in px; the glyph and radius scale with it
 * (32px → 8px radius / 18px glyph in the sidebar, 48px → 12px / 26px on the
 * login card, matching the mockups).
 */
export function BrandMark({ size = 36, className }: { size?: number; className?: string }) {
  const { family } = useTheme();
  if (family === "steaan") {
    return (
      <span
        aria-hidden
        className={cn(
          "inline-flex shrink-0 select-none items-center justify-center bg-primary text-primary-foreground",
          className,
        )}
        style={{ width: size, height: size, borderRadius: Math.round(size / 4) }}
      >
        <Ticket style={{ width: Math.round(size * 0.56), height: Math.round(size * 0.56) }} strokeWidth={2} />
      </span>
    );
  }
  return (
    <img
      src={ticksyMark}
      alt=""
      aria-hidden="true"
      draggable={false}
      className={cn("shrink-0 select-none", className)}
      style={{ width: size, height: size }}
    />
  );
}

/**
 * Login-card header: Nebula shows the Ticksy wordmark (light/dark asset);
 * Steaan shows the teal mark with the product name next to it.
 */
export function BrandWordmark({ className }: { className?: string }) {
  const { family, mode } = useTheme();
  if (family === "steaan") {
    return (
      <span className={cn("inline-flex items-center gap-3 py-3", className)}>
        <BrandMark size={48} />
        <span className="font-display text-2xl font-bold tracking-tight text-foreground">Servicedesk</span>
      </span>
    );
  }
  return (
    <img
      src={mode === "dark" ? ticksyWordmarkDark : ticksyWordmarkLight}
      alt="Ticksy"
      draggable={false}
      className={cn("h-24 w-auto select-none", className)}
    />
  );
}
