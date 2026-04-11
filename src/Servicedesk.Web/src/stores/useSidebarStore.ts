import { create } from "zustand";

type SidebarState = {
  collapsed: boolean;
  toggle: () => void;
  setCollapsed: (value: boolean) => void;
};

// Session-only: no persist. The plan decided to wait for a user-settings
// table (post-v0.0.4) before remembering UI preferences across refreshes.
export const useSidebarStore = create<SidebarState>((set) => ({
  collapsed: false,
  toggle: () => set((s) => ({ collapsed: !s.collapsed })),
  setCollapsed: (value) => set({ collapsed: value }),
}));
