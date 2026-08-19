/**
 * UI theme identifiers — the single vocabulary shared by the server
 * preference (`user_preferences.ui:theme`), the instance default
 * (`Ui.DefaultTheme`), the anti-FOUC bootstrap in `index.html` and the
 * `ThemeProvider`.
 *
 *  - `steaan` — the flat, light-only Steaan house style (v0.0.108). Factory
 *    default for new installs and for users who never made a choice.
 *  - `light` / `dark` — the two modes of the original "Nebula" glass theme
 *    (v0.0.44). Identifiers kept as-is so existing preference rows stay valid.
 *
 * A theme resolves to a *family* (which token set paints the app) and a
 * *mode* (the light/dark orientation components care about). Steaan has no
 * dark mode by design — the source design system has no dark palette and
 * we deliberately do not invent one (ROADMAP decision, 2026-08-19).
 */
export type UiTheme = "steaan" | "light" | "dark";
export type ThemeFamily = "steaan" | "nebula";
export type ThemeMode = "light" | "dark";

export const UI_THEMES: readonly UiTheme[] = ["steaan", "light", "dark"];
export const FACTORY_THEME: UiTheme = "steaan";

/** localStorage key read by the inline bootstrap script in index.html. */
export const THEME_STORAGE_KEY = "sd-theme";

export function isUiTheme(value: unknown): value is UiTheme {
  return value === "steaan" || value === "light" || value === "dark";
}

export function themeFamily(theme: UiTheme): ThemeFamily {
  return theme === "steaan" ? "steaan" : "nebula";
}

export function themeMode(theme: UiTheme): ThemeMode {
  return theme === "dark" ? "dark" : "light";
}

export const THEME_LABELS: Record<UiTheme, string> = {
  steaan: "Steaan",
  light: "Nebula · Light",
  dark: "Nebula · Dark",
};
