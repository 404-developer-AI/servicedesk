import { useSyncExternalStore } from "react";
import { isRole, type Role } from "@/lib/roles";

const STORAGE_KEY = "dev.role";
const DEFAULT_ROLE: Role = "Admin";

function read(): Role {
  if (typeof window === "undefined") return DEFAULT_ROLE;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return isRole(raw) ? raw : DEFAULT_ROLE;
  } catch {
    return DEFAULT_ROLE;
  }
}

const listeners = new Set<() => void>();

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export const devRoleStore = {
  get: read,
  set(role: Role) {
    try {
      window.localStorage.setItem(STORAGE_KEY, role);
    } catch {
      // ignore quota / private-mode errors
    }
    listeners.forEach((l) => l());
  },
};

export function useDevRole(): Role {
  return useSyncExternalStore(subscribe, read, () => DEFAULT_ROLE);
}
