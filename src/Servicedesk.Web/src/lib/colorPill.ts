import type { CSSProperties } from "react";
import type { ThemeFamily, ThemeMode } from "@/lib/theme";

/**
 * Inline style for a chip tinted from a DB-provided status / priority /
 * group colour (hex), per theme. One formula for every caller so the
 * ticket list, grouped list, entity ticket lists and search results stay
 * identical:
 *
 *  - Nebula dark: the colour at 12% alpha behind the colour itself — a soft
 *    glow badge on the deep canvas.
 *  - Nebula light: 22% tint behind the colour darkened 45% so it reads on
 *    a near-white surface.
 *  - Steaan: a flat "50-tone" tint (12% of the colour mixed into white) with
 *    "700-tone" text (the colour darkened 38%), 6px radius and a hairline of
 *    the colour — the tinted-chip translation from the Steaan design spec.
 */
export function colorPillStyle(
  color: string,
  theme: { family: ThemeFamily; mode: ThemeMode },
): CSSProperties {
  if (theme.family === "steaan") {
    return {
      backgroundColor: `color-mix(in srgb, ${color} 12%, white)`,
      color: `color-mix(in srgb, ${color}, black 38%)`,
      borderColor: `color-mix(in srgb, ${color} 28%, white)`,
      borderRadius: 6,
    };
  }
  if (theme.mode === "light") {
    return {
      backgroundColor: `color-mix(in srgb, ${color} 22%, transparent)`,
      color: `color-mix(in srgb, ${color}, black 45%)`,
      borderColor: `color-mix(in srgb, ${color} 40%, transparent)`,
    };
  }
  return {
    backgroundColor: `${color}20`,
    color,
    borderColor: `${color}40`,
  };
}
