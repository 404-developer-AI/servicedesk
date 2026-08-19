import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { authStore } from "@/auth/authStore";
import { preferencesApi, systemApi } from "@/lib/api";
import {
  FACTORY_THEME,
  THEME_STORAGE_KEY,
  isUiTheme,
  themeFamily,
  themeMode,
  type ThemeFamily,
  type ThemeMode,
  type UiTheme,
} from "@/lib/theme";

export type Theme = UiTheme;

type ThemeContextValue = {
  /** The selected theme identifier (what is persisted). */
  theme: Theme;
  /** Which token set paints the app: `steaan` (flat) or `nebula` (glass). */
  family: ThemeFamily;
  /** Light/dark orientation. Steaan is always `light`. */
  mode: ThemeMode;
  setTheme: (t: Theme) => void;
  /**
   * Flips Nebula between Light and Dark. A no-op while Steaan is active —
   * Steaan is light-only by design and the toggle UI is hidden there.
   */
  toggle: () => void;
};

const ThemeContext = createContext<ThemeContextValue | null>(null);

function readStoredTheme(): Theme {
  if (typeof window === "undefined") return FACTORY_THEME;
  try {
    const raw = window.localStorage.getItem(THEME_STORAGE_KEY);
    return isUiTheme(raw) ? raw : FACTORY_THEME;
  } catch {
    return FACTORY_THEME;
  }
}

/**
 * Mirrors the inline anti-FOUC script in index.html — keep the two in sync.
 * `.dark` selects the Nebula dark token set, `.theme-steaan` the Steaan
 * token set; neither class = Nebula light (the `:root` defaults).
 */
function applyTheme(theme: Theme) {
  if (typeof document === "undefined") return;
  const root = document.documentElement;
  root.classList.toggle("dark", theme === "dark");
  root.classList.toggle("theme-steaan", theme === "steaan");
  root.dataset.theme = theme;
  root.style.colorScheme = themeMode(theme);
}

/**
 * Theme provider. Three themes: `steaan` (flat, light-only house style —
 * the factory default since v0.0.108) and the Nebula glass theme in
 * `light` / `dark` (see ARCHITECTURE.md § Theming).
 *
 * Source of truth, in priority order:
 *  1. The user's server-side preference (user_preferences `ui:theme`), read
 *     on bootstrap from `/api/auth/me` (`user.effectiveTheme`) so the user's
 *     choice follows them across devices.
 *  2. The admin-wide default (`Ui.DefaultTheme`), exposed publicly so the
 *     login page can also paint with the right palette on a brand-new
 *     device with no localStorage cache yet.
 *  3. The localStorage cache (`sd-theme`) — used by the inline FOUC script
 *     in `index.html` to apply the right class before any network call.
 *  4. The factory floor: `steaan`.
 *
 * Writes go to the server (PUT `/api/preferences/ui-theme`) for logged-in
 * users; anonymous users only flip the localStorage cache.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(() => readStoredTheme());
  // The last value known to agree with the server (or the initial cache
  // before hydration). Only an explicit change away from it is PUT — a
  // value that came FROM the server must never bounce straight back.
  // Tracked as a value rather than a one-shot "skip next" flag: when the
  // server-resolved theme equals the cached one, React bails out of the
  // state update and a skip-flag would stay armed, swallowing the user's
  // first real change.
  const lastSyncedRef = useRef<Theme>(theme);

  // Apply theme to the DOM + localStorage on every change.
  useEffect(() => {
    applyTheme(theme);
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, theme);
    } catch {
      // ignore — Safari private mode etc.
    }
  }, [theme]);

  // Push the user's explicit choice to the server when authenticated.
  useEffect(() => {
    if (theme === lastSyncedRef.current) return;
    if (!authStore.get().user) return;
    lastSyncedRef.current = theme;
    void preferencesApi.setUiTheme(theme).catch(() => {
      // Server failure is non-fatal — local state still flips, retry on
      // next change. Toast is left to the caller (Profile picker), which
      // can surface a clear "Could not save preference" message.
    });
  }, [theme]);

  // Hydrate from server on mount: prefer the authenticated user's
  // effectiveTheme; fall back to the admin default for anonymous visits.
  // Re-hydrates when a user signs in mid-session (login page → app) so
  // their saved preference takes over from the anonymous default without
  // a full reload.
  useEffect(() => {
    let cancelled = false;
    const apply = (next: unknown) => {
      if (cancelled || !isUiTheme(next)) return;
      // Don't trip the server-sync effect — this came FROM the server.
      lastSyncedRef.current = next;
      setThemeState(next);
    };

    let hydratedUserId: string | null = null;
    let fetchedDefault = false;
    const hydrate = () => {
      const user = authStore.get().user;
      if (user) {
        if (hydratedUserId === user.id) return;
        hydratedUserId = user.id;
        if (isUiTheme(user.effectiveTheme)) apply(user.effectiveTheme);
        return;
      }
      if (hydratedUserId === null && !fetchedDefault) {
        // Anonymous (login page, setup wizard, public surveys/intake): fetch
        // the admin-wide default. Best-effort — failures keep the current
        // localStorage-cached theme.
        fetchedDefault = true;
        void systemApi
          .defaultTheme()
          .then(({ theme: t }) => {
            // A login may have completed while the request was in flight —
            // the user's own preference wins over the anonymous default.
            if (!authStore.get().user) apply(t);
          })
          .catch(() => {
            /* keep cached */
          });
      }
    };
    hydrate();
    const unsubscribe = authStore.subscribe(hydrate);
    return () => {
      cancelled = true;
      unsubscribe();
    };
  }, []);

  const setTheme = useCallback((t: Theme) => setThemeState(t), []);
  const toggle = useCallback(
    () =>
      setThemeState((prev) =>
        prev === "dark" ? "light" : prev === "light" ? "dark" : prev,
      ),
    [],
  );

  const value = useMemo<ThemeContextValue>(
    () => ({
      theme,
      family: themeFamily(theme),
      mode: themeMode(theme),
      setTheme,
      toggle,
    }),
    [theme, setTheme, toggle],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error("useTheme must be used inside <ThemeProvider>");
  return ctx;
}
