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

export type Theme = "light" | "dark";

const STORAGE_KEY = "sd-theme";
const DEFAULT_THEME: Theme = "light";

type ThemeContextValue = {
  theme: Theme;
  setTheme: (t: Theme) => void;
  toggle: () => void;
};

const ThemeContext = createContext<ThemeContextValue | null>(null);

function readStoredTheme(): Theme {
  if (typeof window === "undefined") return DEFAULT_THEME;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw === "dark" ? "dark" : "light";
  } catch {
    return DEFAULT_THEME;
  }
}

function applyTheme(theme: Theme) {
  if (typeof document === "undefined") return;
  const root = document.documentElement;
  if (theme === "dark") root.classList.add("dark");
  else root.classList.remove("dark");
  root.style.colorScheme = theme;
}

/**
 * Light/dark theme provider. Light is the factory default (no `system`
 * option — see ARCHITECTURE.md § Theming).
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
 *  4. The factory floor: `light`.
 *
 * Writes go to the server (PUT `/api/preferences/ui-theme`) for logged-in
 * users; anonymous users only flip the localStorage cache.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(() => readStoredTheme());
  // Skip the network sync on the very first apply — the value already
  // reflects what the user last picked. We only PUT on explicit changes.
  const skipServerSync = useRef(true);

  // Apply theme to the DOM + localStorage on every change.
  useEffect(() => {
    applyTheme(theme);
    try {
      window.localStorage.setItem(STORAGE_KEY, theme);
    } catch {
      // ignore — Safari private mode etc.
    }
  }, [theme]);

  // Push the user's explicit choice to the server when authenticated.
  useEffect(() => {
    if (skipServerSync.current) {
      skipServerSync.current = false;
      return;
    }
    if (!authStore.get().user) return;
    void preferencesApi.setUiTheme(theme).catch(() => {
      // Server failure is non-fatal — local state still flips, retry on
      // next change. Toast is left to the caller (Profile toggle), which
      // can surface a clear "Could not save preference" message.
    });
  }, [theme]);

  // Hydrate from server on mount: prefer the authenticated user's
  // effectiveTheme; fall back to the admin default for anonymous visits.
  useEffect(() => {
    let cancelled = false;
    const apply = (next: Theme) => {
      if (cancelled) return;
      // Don't trip the server-sync effect — this came FROM the server.
      skipServerSync.current = true;
      setThemeState(next);
    };

    const user = authStore.get().user;
    if (user?.effectiveTheme === "light" || user?.effectiveTheme === "dark") {
      apply(user.effectiveTheme);
      return () => {
        cancelled = true;
      };
    }
    // Anonymous (login page, setup wizard, public surveys/intake): fetch
    // the admin-wide default. Best-effort — failures keep the current
    // localStorage-cached theme.
    void systemApi
      .defaultTheme()
      .then(({ theme: t }) => apply(t))
      .catch(() => {
        /* keep cached */
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const setTheme = useCallback((t: Theme) => setThemeState(t), []);
  const toggle = useCallback(
    () => setThemeState((prev) => (prev === "dark" ? "light" : "dark")),
    [],
  );

  const value = useMemo(() => ({ theme, setTheme, toggle }), [theme, setTheme, toggle]);

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error("useTheme must be used inside <ThemeProvider>");
  return ctx;
}
