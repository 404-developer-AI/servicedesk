import { settingsApi } from "@/lib/api";

/// Mobile web (staff). Decides whether a staff session on this device uses
/// the lightweight /m views or the desktop app.
///
/// - Device: a touch-first device — the primary pointer is coarse (phones
///   and tablets). A touch laptop with a mouse/trackpad reports a fine
///   primary pointer and keeps the desktop app.
/// - Admin switch: `Ui.MobileAutoRedirect` (agent-readable projection
///   /api/settings/mobile). Unknown until fetched → the registered default
///   (on) applies.
/// - Per-device choice: "Use desktop version" / "Use mobile version" is a
///   convenience stored in localStorage. It only picks between two views
///   of the same session; nothing security-relevant depends on it.

export type MobilePreference = "mobile" | "desktop";

const PREFERENCE_KEY = "sd-mobile-mode";

let serverAutoRedirect: boolean | null = null;
let settingsFetch: Promise<void> | null = null;

export function isTouchDevice(): boolean {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return false;
  return window.matchMedia("(pointer: coarse)").matches;
}

export function getMobilePreference(): MobilePreference | null {
  try {
    const raw = window.localStorage.getItem(PREFERENCE_KEY);
    return raw === "mobile" || raw === "desktop" ? raw : null;
  } catch {
    return null;
  }
}

export function setMobilePreference(pref: MobilePreference): void {
  try {
    window.localStorage.setItem(PREFERENCE_KEY, pref);
  } catch {
    // Storage blocked (private mode): the choice just isn't remembered.
  }
}

/// Fetches the admin switch once per page load. Only touch devices ask, so
/// a desktop session never pays for the request.
export function loadMobileSettings(): Promise<void> {
  if (!isTouchDevice()) return Promise.resolve();
  if (!settingsFetch) {
    settingsFetch = settingsApi
      .mobile()
      .then((s) => {
        serverAutoRedirect = s.autoRedirect;
      })
      .catch(() => {
        // Keep the default; a failed read must not strand anyone.
        settingsFetch = null;
      });
  }
  return settingsFetch;
}

/// Whether a staff session on this device should land on the /m views.
/// Only an explicit per-device choice overrides the automatic rule; simply
/// visiting /m (by redirect or a pasted link) never persists anything, so the
/// admin switch keeps governing every device that made no choice.
export async function shouldUseMobile(): Promise<boolean> {
  const pref = getMobilePreference();
  if (pref === "desktop") return false;
  if (pref === "mobile") return true;
  if (!isTouchDevice()) return false;
  // Right after sign-in the bootstrap had no session to read the switch
  // with; fetch it before deciding.
  if (serverAutoRedirect === null) await loadMobileSettings();
  return serverAutoRedirect ?? true;
}

export function isMobilePath(path: string): boolean {
  return path === "/m" || path.startsWith("/m/");
}

const TICKET_DETAIL = /^\/tickets\/([0-9a-fA-F-]{36})\/?$/;

/// Maps a desktop location to its mobile counterpart, or null when the page
/// has no mobile view (settings, reports, …) — those stay on the desktop
/// app, which still works on a phone, just not comfortably.
export function mobilePathFor(path: string, searchStr: string, hash = ""): string | null {
  const params = new URLSearchParams(searchStr);
  if (path === "/" || path === "") return "/m";
  if (path === "/tickets") {
    const viewId = params.get("viewId");
    return viewId ? `/m/views/${encodeURIComponent(viewId)}` : "/m";
  }
  const detail = TICKET_DETAIL.exec(path);
  if (detail) return `/m/tickets/${detail[1]}${hash.startsWith("#event-") ? hash : ""}`;
  if (path === "/search") {
    const q = params.get("q");
    return q ? `/m/search?q=${encodeURIComponent(q)}` : "/m/search";
  }
  if (path === "/profile/mentions") return "/m/mentions";
  if (path === "/profile") return "/m/me";
  return null;
}
