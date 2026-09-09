import { toast } from "sonner";
import { router } from "@/app/router";
import { authStore } from "@/auth/authStore";
import { portalAuthStore } from "@/auth/portalAuth";
import { SESSION_EXPIRED_EVENT } from "@/lib/clientVersion";

/// v0.1.5 — turns a mid-session 401 into a clean bounce to the sign-in page
/// instead of the raw "… → 401" error toast an agent used to hit when their
/// session idled out mid-compose (see Security.Session.IdleTimeoutMinutes).
///
/// The 401 is surfaced as a window event by the global fetch wrapper
/// (clientVersion.ts), which already excludes the auth-flow endpoints, so a
/// wrong password or the logged-out boot probes never land here. We clear the
/// matching client auth store — otherwise a stale cached user would let the
/// login gate bounce the viewer straight back and loop — show one notice, and
/// navigate to /login (staff) or /portal/login (customer) with `from` set so
/// the user returns to where they were after signing in.
///
/// Concurrent 401s (a page fires many calls at once) collapse into a single
/// redirect via the `handling` guard; it clears shortly after so a later,
/// genuinely new expiry is handled again.
let handling = false;

export function installSessionExpiryHandler(): void {
  window.addEventListener(SESSION_EXPIRED_EVENT, (event) => {
    const path = (event as CustomEvent<{ path?: string }>).detail?.path ?? "";
    const portal = path.startsWith("/api/portal");
    const loginRoute = portal ? "/portal/login" : "/login";

    if (handling) return;
    // Already on a sign-in / setup surface: nothing to bounce.
    const current = router.state.location.pathname;
    if (
      current === "/login" ||
      current === "/portal/login" ||
      current === "/setup" ||
      current.startsWith("/portal/register") ||
      current.startsWith("/portal/verify-email")
    ) {
      return;
    }

    handling = true;

    // Drop the cached principal so the route gate agrees the viewer is out and
    // renders the login page instead of ping-ponging back to the app.
    if (portal) {
      portalAuthStore.set({ status: "ready", user: null });
    } else {
      authStore.patch({ user: null });
    }

    toast.error("Your session has expired. Please sign in again.");

    void router.navigate({ to: loginRoute, search: { from: current } });

    // Release the guard so a later, genuinely new expiry still redirects.
    window.setTimeout(() => {
      handling = false;
    }, 2000);
  });
}
