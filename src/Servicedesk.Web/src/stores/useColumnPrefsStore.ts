import { create } from "zustand";
import { preferencesApi } from "@/lib/api";

const FALLBACK_COLUMNS = [
  "number",
  "subject",
  "requester",
  "companyName",
  "queueName",
  "statusName",
  "priorityName",
  "assigneeEmail",
  "updatedUtc",
];

type ColumnPrefsState = {
  visibleColumns: string[];
  source: "user-view" | "view" | "user" | "default" | "fallback";
  activeViewId: string | null;
  loaded: boolean;
  setVisibleColumns: (columns: string[]) => void;
  toggleColumn: (column: string) => void;
  resetToDefaults: () => void;
  loadFromServer: (viewId?: string) => Promise<void>;
  setActiveView: (viewId: string | null) => void;
};

function saveToServer(columns: string[], viewId: string | null) {
  preferencesApi.saveColumns(columns.join(","), viewId ?? undefined).catch(() => {});
}

export const useColumnPrefsStore = create<ColumnPrefsState>()((set, get) => ({
  visibleColumns: [...FALLBACK_COLUMNS],
  source: "fallback",
  activeViewId: null,
  loaded: false,

  setVisibleColumns: (columns) => {
    const viewId = get().activeViewId;
    set({ visibleColumns: columns, source: viewId ? "user-view" : "user" });
    saveToServer(columns, viewId);
  },

  toggleColumn: (column) => {
    const { visibleColumns, activeViewId } = get();
    const next = visibleColumns.includes(column)
      ? visibleColumns.filter((c) => c !== column)
      : [...visibleColumns, column];
    set({ visibleColumns: next, source: activeViewId ? "user-view" : "user" });
    saveToServer(next, activeViewId);
  },

  resetToDefaults: () => {
    const viewId = get().activeViewId;
    preferencesApi.resetColumns(viewId ?? undefined).then(async () => {
      try {
        const pref = await preferencesApi.getColumns(viewId ?? undefined);
        const cols = pref.columns.split(",").map((c) => c.trim()).filter(Boolean);
        set({
          visibleColumns: cols.length > 0 ? cols : [...FALLBACK_COLUMNS],
          source: pref.source,
        });
      } catch {
        set({ visibleColumns: [...FALLBACK_COLUMNS], source: "fallback" });
      }
    }).catch(() => {});
  },

  loadFromServer: async (viewId?: string) => {
    try {
      const pref = await preferencesApi.getColumns(viewId);
      const cols = pref.columns.split(",").map((c) => c.trim()).filter(Boolean);
      set({
        visibleColumns: cols.length > 0 ? cols : [...FALLBACK_COLUMNS],
        source: pref.source,
        activeViewId: viewId ?? null,
        loaded: true,
      });
    } catch {
      set({ loaded: true });
    }
  },

  setActiveView: (viewId: string | null) => {
    const current = get().activeViewId;
    if (current === viewId) return;
    set({ activeViewId: viewId, loaded: false });
    get().loadFromServer(viewId ?? undefined);
  },
}));

export { FALLBACK_COLUMNS as DEFAULT_COLUMNS };
