import { useMemo, useState } from "react";
import { Building2, Plus, Search } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import type { IntegrationLinkableCompany } from "@/lib/api";
import { cn } from "@/lib/utils";

/**
 * Compact company picker for the spam-filter / backup rollup links. Lists only
 * Microsoft 365-connected companies (the valid rollup targets), filtered by a
 * search box, with the already-linked ones excluded. Picking one closes the
 * popover and fires onSelect.
 */
export function CompanyLinkPicker({
  companies,
  excludeIds,
  onSelect,
  disabled,
  label = "Link company",
}: {
  companies: IntegrationLinkableCompany[];
  excludeIds: Set<string>;
  onSelect: (companyId: string) => void;
  disabled?: boolean;
  label?: string;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");

  const available = useMemo(() => {
    const needle = query.trim().toLowerCase();
    return companies
      .filter((c) => !excludeIds.has(c.companyId))
      .filter(
        (c) =>
          needle.length === 0 ||
          (c.name ?? "").toLowerCase().includes(needle) ||
          (c.code ?? "").toLowerCase().includes(needle),
      )
      .slice(0, 50);
  }, [companies, excludeIds, query]);

  return (
    <Popover
      open={open}
      onOpenChange={(o) => {
        setOpen(o);
        if (!o) setQuery("");
      }}
    >
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={disabled}
          className={cn(
            "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium transition-colors",
            "border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground",
            "disabled:cursor-not-allowed disabled:opacity-50",
          )}
        >
          <Plus className="h-3 w-3" /> {label}
        </button>
      </PopoverTrigger>

      <PopoverContent className="w-72 p-0" align="start" side="bottom">
        <div className="flex items-center gap-2 border-b border-glass px-3 py-2">
          <Search className="h-3.5 w-3.5 text-muted-foreground" />
          <input
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search companies…"
            spellCheck={false}
            className="w-full bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground/60"
          />
        </div>
        <div className="max-h-64 overflow-y-auto p-1">
          {available.length === 0 ? (
            <p className="px-3 py-4 text-center text-xs text-muted-foreground">
              No Microsoft 365-connected companies match.
            </p>
          ) : (
            available.map((c) => (
              <button
                key={c.companyId}
                type="button"
                onClick={() => {
                  onSelect(c.companyId);
                  setOpen(false);
                  setQuery("");
                }}
                className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm transition-colors hover:bg-glass-hover"
              >
                <Building2 className="h-3.5 w-3.5 shrink-0 text-muted-foreground/60" />
                <span className="flex-1 truncate text-foreground">{c.name ?? "—"}</span>
                {c.code && (
                  <span className="font-mono text-[11px] text-muted-foreground/70">{c.code}</span>
                )}
              </button>
            ))
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}
