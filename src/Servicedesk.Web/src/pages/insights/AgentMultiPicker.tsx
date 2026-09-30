import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Check, ChevronDown, Search, UserPlus, X } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Skeleton } from "@/components/ui/skeleton";
import { userApi } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

/// Picks up to `max` agents for the Agents overview. The order of picking
/// is the order of the comparison (and of the colours), so a toggled-off
/// agent frees its slot for the next pick.
export function AgentMultiPicker({
  selected,
  max,
  names,
  colorFor,
  onChange,
}: {
  selected: string[];
  max: number;
  /** Display names from the report, when loaded (the picker only knows emails). */
  names: Map<string, string>;
  colorFor: (index: number) => string;
  onChange: (ids: string[]) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const [filter, setFilter] = React.useState("");
  const { data: agents } = useQuery({ queryKey: ["agents"], queryFn: userApi.listAgents });

  const full = selected.length >= max;
  const needle = filter.trim().toLowerCase();
  const visible = (agents ?? []).filter(
    (a) => !needle || a.email.toLowerCase().includes(needle) || (names.get(a.id) ?? "").toLowerCase().includes(needle),
  );

  function toggle(id: string) {
    if (selected.includes(id)) onChange(selected.filter((x) => x !== id));
    else if (!full) onChange([...selected, id]);
  }

  function label(id: string) {
    return names.get(id) ?? agents?.find((a) => a.id === id)?.email ?? "…";
  }

  return (
    <div className="flex flex-wrap items-center gap-2">
      {selected.map((id, i) => (
        <span
          key={id}
          className="sd-agent-chip inline-flex h-8 items-center gap-2 rounded-full border border-glass bg-glass pl-2.5 pr-1 text-xs text-foreground"
        >
          <span className="h-2.5 w-2.5 shrink-0 rounded-[3px]" style={{ backgroundColor: colorFor(i) }} />
          <span className="max-w-[12rem] truncate" title={label(id)}>{label(id)}</span>
          <button
            type="button"
            onClick={() => onChange(selected.filter((x) => x !== id))}
            aria-label={`Remove ${label(id)}`}
            className="inline-flex h-6 w-6 items-center justify-center rounded-full text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
          >
            <X className="h-3 w-3" />
          </button>
        </span>
      ))}

      <Popover open={open} onOpenChange={(o) => { setOpen(o); if (!o) setFilter(""); }}>
        <PopoverTrigger asChild>
          <button
            type="button"
            className={cn(
              "inline-flex h-8 items-center gap-1.5 rounded-full border border-dashed border-glass px-3 text-xs font-medium transition-colors",
              "text-muted-foreground hover:bg-glass-hover hover:text-foreground",
            )}
          >
            <UserPlus className="h-3.5 w-3.5" />
            {selected.length === 0 ? "Pick agents" : full ? "Change" : "Add agent"}
            <span className="tabular-nums text-muted-foreground/70">{selected.length}/{max}</span>
            <ChevronDown className="h-3 w-3 opacity-50" />
          </button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-[320px] border-glass p-0 glass-card">
          <div className="flex items-center gap-2 border-b border-glass px-3 py-2">
            <Search className="h-3.5 w-3.5 text-muted-foreground" />
            <input
              autoFocus
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder="Find an agent"
              aria-label="Find an agent"
              className="h-7 flex-1 bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground"
            />
          </div>
          <div className="max-h-[300px] overflow-y-auto p-1">
            {!agents ? (
              <div className="space-y-1 p-1">
                {[0, 1, 2].map((i) => <Skeleton key={i} className="h-8 w-full" />)}
              </div>
            ) : visible.length === 0 ? (
              <p className="px-3 py-2 text-sm text-muted-foreground">No agents match.</p>
            ) : (
              visible.map((a) => {
                const index = selected.indexOf(a.id);
                const on = index >= 0;
                const blocked = !on && full;
                return (
                  <button
                    key={a.id}
                    type="button"
                    role="menuitemcheckbox"
                    aria-checked={on}
                    disabled={blocked}
                    onClick={() => toggle(a.id)}
                    title={blocked ? `You can compare up to ${max} agents` : undefined}
                    className={cn(
                      "flex w-full items-center gap-2.5 rounded-[calc(var(--radius)-2px)] px-2.5 py-2 text-left text-sm transition-colors",
                      "hover:bg-glass-hover disabled:cursor-not-allowed disabled:opacity-45 disabled:hover:bg-transparent",
                    )}
                  >
                    <span
                      className={cn(
                        "flex h-4 w-4 shrink-0 items-center justify-center rounded-[4px] border",
                        on ? "border-transparent text-white" : "border-glass",
                      )}
                      style={on ? { backgroundColor: colorFor(index) } : undefined}
                    >
                      {on && <Check className="h-3 w-3" strokeWidth={3} />}
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-foreground">{names.get(a.id) ?? a.email}</span>
                      {names.has(a.id) && names.get(a.id) !== a.email && (
                        <span className="block truncate text-[11px] text-muted-foreground">{a.email}</span>
                      )}
                    </span>
                    <span className="shrink-0 text-[10px] uppercase tracking-wide text-muted-foreground">{a.roleName}</span>
                  </button>
                );
              })
            )}
          </div>
          {full && (
            <p className="border-t border-glass px-3 py-2 text-[11px] text-muted-foreground">
              Comparing the maximum of {max}. Remove one to pick another.
            </p>
          )}
        </PopoverContent>
      </Popover>
    </div>
  );
}
