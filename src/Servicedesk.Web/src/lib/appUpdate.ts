/**
 * Reacts to "the server runs a newer version than this bundle".
 *
 * Detection is event-driven, never a timer: a deploy restarts the server,
 * which drops every SignalR connection — the reconnect (seconds after the
 * update) triggers a version re-check. Tab focus is the opt-in second
 * trigger for sessions that slept through the reconnect. The admin decides
 * the reaction via App.UpdateRefresh.Mode: "auto" reloads at a safe moment
 * (no open dialog, nobody mid-typing) and falls back to a persistent toast
 * otherwise; "banner" only ever shows the toast. Reloads keep the user
 * logged in (sessions are server-side) and composer drafts survive via the
 * server-persisted workspace autosave.
 */
import { toast } from "sonner";
import { anchorReferenceVersion, getReferenceVersion } from "@/lib/clientVersion";

/** Dispatched by the presence hub's onreconnected callback. */
export const SERVER_RECONNECTED_EVENT = "app:server-reconnected";

const UPDATE_TOAST_ID = "app-update-available";

// Guards the pathological loop where a reload lands on the same stale
// bundle (e.g. a proxy still caching index.html): auto-reload fires at most
// once per target version per tab; after that only the toast shows.
const RELOADED_FOR_KEY = "sd-update-reloaded-for";

let reloadPending = false;

export function handleServerVersion(serverVersion: string, refreshMode: string): void {
  if (!serverVersion || reloadPending) {
    return;
  }

  const reference = getReferenceVersion();
  if (!reference) {
    // Dev bundle without a baked version: first observation becomes ours.
    anchorReferenceVersion(serverVersion);
    return;
  }
  if (serverVersion === reference) {
    return;
  }

  let alreadyReloadedForTarget = false;
  try {
    alreadyReloadedForTarget = sessionStorage.getItem(RELOADED_FOR_KEY) === serverVersion;
  } catch {
    // Storage unavailable — behave as if we never reloaded.
  }

  if (refreshMode === "auto" && !alreadyReloadedForTarget && canReloadSafely()) {
    try {
      sessionStorage.setItem(RELOADED_FOR_KEY, serverVersion);
    } catch {
      // Best effort; without storage we rely on reloadPending for this tab.
    }
    reloadPending = true;
    window.location.reload();
    return;
  }

  showUpdateToast();
}

/**
 * A write was rejected with 426: the server no longer accepts this bundle's
 * contract. Deliberately NOT an automatic reload — the user's last action
 * was lost and they may have text on screen worth copying first. Nothing
 * can be written anyway, so staying is safe, just useless.
 */
export function handleClientOutdated(): void {
  if (reloadPending) {
    return;
  }
  toast.error("Update required", {
    id: UPDATE_TOAST_ID,
    duration: Infinity,
    description:
      "The server was updated and your last action was not saved. Copy any unsaved text, then reload to continue.",
    action: {
      label: "Reload",
      onClick: () => window.location.reload(),
    },
  });
}

function showUpdateToast(): void {
  toast.message("New version available", {
    id: UPDATE_TOAST_ID,
    duration: Infinity,
    description: "Servicedesk was updated. Reload to get the latest version.",
    action: {
      label: "Reload",
      onClick: () => window.location.reload(),
    },
  });
}

/**
 * Safe = nothing on screen the user would lose by a reload right now:
 * no open dialog/sheet/drawer (Radix, vaul and the hand-rolled ones all
 * render role="dialog") and no focused text-entry element.
 */
export function canReloadSafely(root: Document = document): boolean {
  if (root.querySelector('[role="dialog"]')) {
    return false;
  }
  const active = root.activeElement;
  if (active instanceof HTMLElement) {
    if (active.isContentEditable) {
      return false;
    }
    const tag = active.tagName;
    if (tag === "TEXTAREA" || tag === "INPUT" || tag === "SELECT") {
      return false;
    }
  }
  return true;
}
