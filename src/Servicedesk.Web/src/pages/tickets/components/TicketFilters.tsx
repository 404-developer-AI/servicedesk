import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Search, X } from "lucide-react";
import { taxonomyApi } from "@/lib/api";
import { AgentPicker } from "@/components/AgentPicker";
import { cn } from "@/lib/utils";
import type { TicketListQuery } from "@/lib/ticket-api";

type TicketFiltersProps = {
  filters: TicketListQuery;
  onChange: (filters: TicketListQuery) => void;
};

const SELECT_CLASS =
  "h-8 px-2 text-sm rounded-md border border-white/10 bg-white/[0.04] text-foreground outline-none focus:border-primary/60 cursor-pointer";

export function TicketFilters({ filters, onChange }: TicketFiltersProps) {
  const [searchInput, setSearchInput] = React.useState(filters.search ?? "");

  const { data: queues } = useQuery({
    queryKey: ["queues"],
    queryFn: taxonomyApi.queues.list,
    staleTime: 300_000,
  });

  const { data: statuses } = useQuery({
    queryKey: ["statuses"],
    queryFn: taxonomyApi.statuses.list,
    staleTime: 300_000,
  });

  const { data: priorities } = useQuery({
    queryKey: ["priorities"],
    queryFn: taxonomyApi.priorities.list,
    staleTime: 300_000,
  });

  React.useEffect(() => {
    const id = setTimeout(() => {
      if (searchInput !== (filters.search ?? "")) {
        onChange({ ...filters, search: searchInput || undefined, cursorUpdatedUtc: undefined, cursorId: undefined });
      }
    }, 300);
    return () => clearTimeout(id);
  }, [searchInput]); // eslint-disable-line react-hooks/exhaustive-deps

  const hasActiveFilters =
    !!filters.queueId ||
    !!filters.statusId ||
    !!filters.priorityId ||
    !!filters.assigneeUserId ||
    !!filters.search ||
    !!filters.openOnly;

  function clearFilters() {
    setSearchInput("");
    onChange({});
  }

  return (
    <div className="glass-panel p-3 flex items-center gap-3 flex-wrap">
      <div className="relative flex items-center">
        <Search className="absolute left-2 h-3.5 w-3.5 text-muted-foreground pointer-events-none" />
        <input
          type="text"
          placeholder="Search tickets..."
          value={searchInput}
          onChange={(e) => setSearchInput(e.target.value)}
          className={cn(
            SELECT_CLASS,
            "h-8 pl-7 pr-2 w-48",
          )}
        />
      </div>

      <select
        value={filters.queueId ?? ""}
        onChange={(e) =>
          onChange({ ...filters, queueId: e.target.value || undefined, cursorUpdatedUtc: undefined, cursorId: undefined })
        }
        className={SELECT_CLASS}
      >
        <option value="" className="bg-background">All queues</option>
        {queues?.map((q) => (
          <option key={q.id} value={q.id} className="bg-background">
            {q.name}
          </option>
        ))}
      </select>

      <select
        value={filters.statusId ?? ""}
        onChange={(e) =>
          onChange({ ...filters, statusId: e.target.value || undefined, cursorUpdatedUtc: undefined, cursorId: undefined })
        }
        className={SELECT_CLASS}
      >
        <option value="" className="bg-background">All statuses</option>
        {statuses?.map((s) => (
          <option key={s.id} value={s.id} className="bg-background">
            {s.name}
          </option>
        ))}
      </select>

      <select
        value={filters.priorityId ?? ""}
        onChange={(e) =>
          onChange({ ...filters, priorityId: e.target.value || undefined, cursorUpdatedUtc: undefined, cursorId: undefined })
        }
        className={SELECT_CLASS}
      >
        <option value="" className="bg-background">All priorities</option>
        {priorities?.map((p) => (
          <option key={p.id} value={p.id} className="bg-background">
            {p.name}
          </option>
        ))}
      </select>

      <div className="w-48">
        <AgentPicker
          value={filters.assigneeUserId ?? null}
          onChange={(userId) =>
            onChange({ ...filters, assigneeUserId: userId ?? undefined, cursorUpdatedUtc: undefined, cursorId: undefined })
          }
          placeholder="Any assignee"
          className="h-8 text-sm"
        />
      </div>

      <label className="flex items-center gap-1.5 text-sm text-muted-foreground cursor-pointer select-none">
        <input
          type="checkbox"
          checked={filters.openOnly ?? false}
          onChange={(e) =>
            onChange({ ...filters, openOnly: e.target.checked || undefined, cursorUpdatedUtc: undefined, cursorId: undefined })
          }
          className="rounded border-white/20 bg-white/[0.04] accent-primary"
        />
        Open only
      </label>

      {hasActiveFilters && (
        <button
          type="button"
          onClick={clearFilters}
          className="ml-auto flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground transition-colors"
        >
          <X className="h-3.5 w-3.5" />
          Clear filters
        </button>
      )}
    </div>
  );
}
