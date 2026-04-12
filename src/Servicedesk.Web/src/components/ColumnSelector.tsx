import * as React from "react";
import { Columns3, RotateCcw } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useColumnPrefsStore, DEFAULT_COLUMNS } from "@/stores/useColumnPrefsStore";
import { cn } from "@/lib/utils";

const ALL_COLUMNS: { id: string; label: string }[] = [
  { id: "number", label: "Number" },
  { id: "subject", label: "Subject" },
  { id: "requester", label: "Requester" },
  { id: "companyName", label: "Company" },
  { id: "queueName", label: "Queue" },
  { id: "statusName", label: "Status" },
  { id: "priorityName", label: "Priority" },
  { id: "categoryName", label: "Category" },
  { id: "assigneeEmail", label: "Assignee" },
  { id: "createdUtc", label: "Created" },
  { id: "updatedUtc", label: "Updated" },
  { id: "dueUtc", label: "Due" },
];

export function ColumnSelector() {
  const { visibleColumns, toggleColumn, resetToDefaults } = useColumnPrefsStore();
  const [open, setOpen] = React.useState(false);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className={cn(
            "flex items-center gap-1.5 h-8 px-3 rounded-md border border-white/10",
            "bg-white/[0.04] text-sm text-muted-foreground",
            "hover:bg-white/[0.07] hover:text-foreground transition-colors",
          )}
        >
          <Columns3 className="h-3.5 w-3.5" />
          Columns
        </button>
      </PopoverTrigger>

      <PopoverContent className="w-52 p-2" align="end">
        <div className="space-y-0.5">
          {ALL_COLUMNS.map((col) => {
            const checked = visibleColumns.includes(col.id);
            return (
              <label
                key={col.id}
                className="flex items-center gap-2.5 rounded px-2 py-1.5 text-sm cursor-pointer hover:bg-white/[0.07] transition-colors"
              >
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => toggleColumn(col.id)}
                  className="rounded border-white/20 bg-white/[0.04] accent-primary"
                />
                <span className={checked ? "text-foreground" : "text-muted-foreground"}>
                  {col.label}
                </span>
              </label>
            );
          })}
        </div>

        <div className="mt-2 border-t border-white/10 pt-2">
          <button
            type="button"
            onClick={() => {
              resetToDefaults();
              setOpen(false);
            }}
            className="flex w-full items-center gap-1.5 rounded px-2 py-1.5 text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.07] transition-colors"
          >
            <RotateCcw className="h-3 w-3" />
            Reset to defaults
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

export { DEFAULT_COLUMNS };
