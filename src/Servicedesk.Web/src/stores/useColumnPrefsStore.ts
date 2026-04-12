import { create } from "zustand";
import { persist } from "zustand/middleware";

const DEFAULT_COLUMNS = [
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
  setVisibleColumns: (columns: string[]) => void;
  toggleColumn: (column: string) => void;
  resetToDefaults: () => void;
};

export const useColumnPrefsStore = create<ColumnPrefsState>()(
  persist(
    (set) => ({
      visibleColumns: [...DEFAULT_COLUMNS],
      setVisibleColumns: (columns) => set({ visibleColumns: columns }),
      toggleColumn: (column) =>
        set((s) => ({
          visibleColumns: s.visibleColumns.includes(column)
            ? s.visibleColumns.filter((c) => c !== column)
            : [...s.visibleColumns, column],
        })),
      resetToDefaults: () => set({ visibleColumns: [...DEFAULT_COLUMNS] }),
    }),
    { name: "sd-column-prefs" },
  ),
);

export { DEFAULT_COLUMNS };
