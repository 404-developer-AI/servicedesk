import { portalAuthApi, type PortalMeUser } from "@/lib/portal-api";

/// v0.1.1 — the customer portal rides its own session cookie (`sd_portal`),
/// so the staff `authStore` (fed by /api/auth/me) never sees a portal
/// session anymore. The portal route gates read THIS store instead, fed by
/// /api/portal/auth/me: the customer's own session or an admin's read-only
/// shadow view. Primed by bootstrapAuth() on portal paths before the router
/// mounts; refreshed after portal login / logout.
type PortalAuthState = {
  status: "idle" | "ready";
  user: PortalMeUser | null;
};

let state: PortalAuthState = { status: "idle", user: null };
const listeners = new Set<() => void>();

export const portalAuthStore = {
  get: (): PortalAuthState => state,
  set: (next: PortalAuthState): void => {
    state = next;
    listeners.forEach((l) => l());
  },
  subscribe: (l: () => void): (() => void) => {
    listeners.add(l);
    return () => listeners.delete(l);
  },
};

export async function refreshPortalAuth(): Promise<void> {
  try {
    const me = await portalAuthApi.me();
    portalAuthStore.set({ status: "ready", user: me.user });
  } catch {
    portalAuthStore.set({ status: "ready", user: null });
  }
}
