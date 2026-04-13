import { create } from "zustand";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";

type SidebarState = {
  collapsed: boolean;
  toggle: () => void;
  setCollapsed: (value: boolean) => void;
};

export const useSidebarStore = create<SidebarState>((set) => ({
  collapsed: false,
  toggle: () =>
    set((s) => {
      const next = !s.collapsed;
      useWorkspaceStore.getState().setSidebarCollapsed(next);
      return { collapsed: next };
    }),
  setCollapsed: (value) => {
    useWorkspaceStore.getState().setSidebarCollapsed(value);
    set({ collapsed: value });
  },
}));
