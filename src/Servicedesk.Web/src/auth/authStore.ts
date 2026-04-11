import { useSyncExternalStore } from "react";
import type { Role } from "@/lib/roles";

export type AuthUser = {
  id: string;
  email: string;
  role: Role;
  amr: string;
  twoFactorEnabled: boolean;
};

export type AuthState = {
  status: "loading" | "ready";
  user: AuthUser | null;
  setupAvailable: boolean;
};

let state: AuthState = {
  status: "loading",
  user: null,
  setupAvailable: false,
};

const listeners = new Set<() => void>();

function emit() {
  listeners.forEach((l) => l());
}

export const authStore = {
  get: () => state,
  set(next: AuthState) {
    state = next;
    emit();
  },
  patch(partial: Partial<AuthState>) {
    state = { ...state, ...partial };
    emit();
  },
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};

export function useAuth(): AuthState {
  return useSyncExternalStore(authStore.subscribe, authStore.get, authStore.get);
}
