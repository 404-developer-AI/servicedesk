import { useEffect, useRef } from "react";
import { useRouterState } from "@tanstack/react-router";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";

/**
 * Sets up workspace auto-save listeners:
 * 1. visibilitychange — flush when tab is hidden / minimized
 * 2. beforeunload — fire-and-forget save on browser close / refresh
 * 3. route changes — flush on in-app navigation
 *
 * Mount once in AppShell.
 */
export function useWorkspaceAutoSave() {
  // Flush on visibility change (tab switch, minimize, alt-tab)
  useEffect(() => {
    const handler = () => {
      if (document.visibilityState === "hidden") {
        useWorkspaceStore.getState().flush();
      }
    };
    document.addEventListener("visibilitychange", handler);
    return () => document.removeEventListener("visibilitychange", handler);
  }, []);

  // Fire-and-forget on page unload (browser close, refresh, tab close)
  useEffect(() => {
    const handler = () => {
      useWorkspaceStore.getState().flushSync();
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, []);

  // Flush on route changes
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const prevPathRef = useRef(pathname);

  useEffect(() => {
    if (prevPathRef.current !== pathname) {
      useWorkspaceStore.getState().flush();
      prevPathRef.current = pathname;
    }
  }, [pathname]);
}
