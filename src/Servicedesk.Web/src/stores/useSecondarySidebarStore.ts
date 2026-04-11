import type { ReactNode } from "react";
import { create } from "zustand";

type SecondarySidebarState = {
  /** ReactNode rendered as a flex sibling of the main Sidebar, or null. */
  content: ReactNode | null;
  set: (content: ReactNode | null) => void;
};

// Lets nested routes (currently just /settings/*) inject a second sidebar
// into the AppShell's root flex row, where it can share the main Sidebar's
// `m-3 h-[calc(100vh-1.5rem)]` frame and therefore match its top/bottom
// exactly. Rendering the rail from inside <main> can't achieve that — the
// Header always pushes it down.
export const useSecondarySidebarStore = create<SecondarySidebarState>((set) => ({
  content: null,
  set: (content) => set({ content }),
}));
