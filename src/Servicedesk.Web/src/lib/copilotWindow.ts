// One reusable browser window for the Copilot launcher (v0.0.89). Microsoft
// blocks framing its Copilot/login pages (X-Frame-Options / frame-ancestors),
// so an in-page iframe is impossible — the launcher opens the *real* Copilot in
// a separate window instead. Every click targets the same fixed window name, so
// the first click opens it and later clicks just refocus / reuse that same
// window rather than stacking tabs. No Servicedesk data is ever passed; this is
// a pure shortcut to Copilot, which the agent reaches via their M365 session.
const COPILOT_WINDOW_NAME = "servicedesk-copilot";

// Side-panel-sized popup. Width is a sensible fixed reading width; the height
// fills the available screen so it reads as a docked panel next to the app.
const POPUP_WIDTH = 480;

/// Open (or refocus) the shared Copilot window on the given URL. When
/// `openInPopup` is true it opens a focused, side-panel-sized popup pinned to
/// the right of the screen; otherwise a normal new tab. No-op without a URL.
export function openCopilotWindow(url: string, openInPopup: boolean): void {
  if (!url) return;
  // Defense in depth (audit v0.1.1 #9): the server validates Copilot.Url to
  // https on write AND on read, but this value still crosses a privilege
  // boundary (admin-set, runs in every agent's browser via window.open) — a
  // javascript:/data: URL must never reach window.open from here either.
  try {
    if (new URL(url).protocol !== "https:") return;
  } catch {
    return;
  }

  if (!openInPopup) {
    const tab = window.open(url, COPILOT_WINDOW_NAME);
    tab?.focus();
    return;
  }

  const width = Math.min(POPUP_WIDTH, window.screen.availWidth);
  const height = window.screen.availHeight;
  // Pin to the right edge of the available screen, falling back to 0 if the
  // numbers are unavailable (some browsers return 0 for availWidth).
  const left = Math.max(0, window.screen.availWidth - width);
  // Note: no `noopener` here on purpose — severing the opener puts the popup
  // in another browsing-context group, which breaks the named-window reuse
  // (every click would stack a fresh window). The URL is server-validated to
  // https plus scheme-guarded above, which is what actually bounds the risk.
  const features = `popup=yes,width=${width},height=${height},left=${left},top=0`;
  const win = window.open(url, COPILOT_WINDOW_NAME, features);
  // Pop-up blockers can return null; nothing to focus then.
  win?.focus();
}
