import * as React from "react";
import { Variable, Search, Calendar, Type } from "lucide-react";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Button } from "@/components/ui/button";
import type { TriggerTemplateVariable } from "@/lib/api";
import { cn } from "@/lib/utils";

type Props = {
  variables: TriggerTemplateVariable[];
  onPick: (placeholder: string) => void;
};

/// "Insert variable" popover — shared by every templating field on the
/// trigger editor. Picks a whitelisted variable from the server-supplied
/// catalog and forwards the rendered placeholder to the caller (a string
/// like `#{ticket.number}` or `#{dt(ticket.due_utc, "yyyy-MM-dd HH:mm", "Europe/Brussels")}`).
/// Plays the same role as the `::` Tiptap suggestion in the spec — kept as
/// a popover-on-button so plain-text fields (subject) and HTML fields
/// (body_html) share the same insertion path without forking the editor.
export function TemplateVariablePicker({ variables, onPick }: Props) {
  const [open, setOpen] = React.useState(false);
  const [query, setQuery] = React.useState("");

  const filtered = React.useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return variables;
    return variables.filter(
      (v) =>
        v.path.toLowerCase().includes(q) ||
        v.label.toLowerCase().includes(q),
    );
  }, [variables, query]);

  function buildPlaceholder(v: TriggerTemplateVariable): string {
    if (v.type === "datetime") {
      return `#{dt(${v.path}, "yyyy-MM-dd HH:mm", "Europe/Brussels")}`;
    }
    return `#{${v.path}}`;
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          type="button"
          size="sm"
          variant="ghost"
          className="h-7 gap-1.5 text-xs text-muted-foreground hover:text-foreground"
        >
          <Variable className="h-3.5 w-3.5" />
          Insert variable
        </Button>
      </PopoverTrigger>
      <PopoverContent
        align="end"
        className="w-80 p-0 border border-white/10 bg-[hsl(240_10%_6%/0.96)] backdrop-blur-xl"
      >
        <div className="border-b border-white/[0.06] px-2 py-2">
          <div className="relative">
            <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground/60" />
            <input
              autoFocus
              type="text"
              placeholder="Search variables…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="w-full rounded-md border border-white/10 bg-white/[0.04] py-1.5 pl-7 pr-2 text-xs text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
            />
          </div>
        </div>
        <div className="max-h-72 overflow-auto py-1">
          {filtered.length === 0 ? (
            <div className="px-3 py-2 text-xs text-muted-foreground/70">
              No variables match
            </div>
          ) : (
            filtered.map((v) => (
              <button
                key={v.path}
                type="button"
                onClick={() => {
                  onPick(buildPlaceholder(v));
                  setOpen(false);
                  setQuery("");
                }}
                className={cn(
                  "w-full flex items-start gap-2 px-3 py-1.5 text-left transition-colors",
                  "text-foreground/85 hover:bg-white/[0.04]",
                )}
              >
                <span className="mt-0.5 text-muted-foreground/60">
                  {v.type === "datetime" ? (
                    <Calendar className="h-3.5 w-3.5" />
                  ) : (
                    <Type className="h-3.5 w-3.5" />
                  )}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-xs font-medium">
                    {v.label}
                  </span>
                  <span className="block truncate font-mono text-[10px] text-muted-foreground/70">
                    {buildPlaceholder(v)}
                  </span>
                </span>
              </button>
            ))
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}
