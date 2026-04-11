import { authApi } from "@/lib/api";
import { authStore, type AuthUser } from "@/auth/authStore";

/// Fetches `/api/auth/setup/status` + `/api/auth/me` before the router takes
/// over, so route gates can trust `authStore.get()` from their very first call.
/// Network failures fall through to an unauthenticated, non-setup state — the
/// login page then surfaces the real error on submit.
export async function bootstrapAuth(): Promise<void> {
  try {
    const [setup, me] = await Promise.all([
      authApi.setupStatus(),
      authApi.me(),
    ]);
    authStore.set({
      status: "ready",
      user: me.user as AuthUser | null,
      setupAvailable: setup.available,
    });
  } catch {
    authStore.set({ status: "ready", user: null, setupAvailable: false });
  }
}

export async function refreshAuth(): Promise<void> {
  try {
    const me = await authApi.me();
    authStore.patch({ user: me.user as AuthUser | null });
  } catch {
    authStore.patch({ user: null });
  }
}
