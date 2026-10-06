import * as React from "react";
import { ArrowDown, ArrowUp, Info, Search } from "lucide-react";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
import { CopyButton } from "@/components/CopyButton";
import { cn } from "@/lib/utils";
import { TONE, type Tone } from "./perfFormat";

// Building blocks of the Performance page. Every metric carries an (i)
// explaining what it is and when it is bad — the page is meant to be read
// by an owner, not only by a performance engineer.

export function InfoTip({ text, className }: { text: string; className?: string }) {
  return (
    <TooltipProvider delayDuration={150}>
      <Tooltip>
        <TooltipTrigger asChild>
          <button
            type="button"
            aria-label="What is this?"
            className={cn("inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full text-muted-foreground/70 transition-colors hover:text-foreground", className)}
          >
            <Info className="h-3.5 w-3.5" />
          </button>
        </TooltipTrigger>
        <TooltipContent side="top" className="max-w-xs whitespace-normal text-left leading-relaxed">
          {text}
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}

export function Panel({
  title,
  info,
  actions,
  children,
  className,
  bodyClassName,
}: {
  title?: React.ReactNode;
  info?: string;
  actions?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
  bodyClassName?: string;
}) {
  return (
    <section className={cn("glass-card rounded-lg border border-glass-strong bg-glass", className)}>
      {title || actions ? (
        <header className="flex items-center justify-between gap-3 border-b border-glass px-4 py-3">
          <div className="flex min-w-0 items-center gap-1.5">
            <h3 className="truncate text-sm font-semibold text-foreground">{title}</h3>
            {info ? <InfoTip text={info} /> : null}
          </div>
          {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
        </header>
      ) : null}
      <div className={cn("p-4", bodyClassName)}>{children}</div>
    </section>
  );
}

export function Pill({ tone, children, className }: { tone: Tone; children: React.ReactNode; className?: string }) {
  return (
    <span className={cn("inline-flex items-center gap-1 whitespace-nowrap rounded-md border px-1.5 py-0.5 text-[11px] font-medium leading-none", TONE[tone].badge, className)}>
      {children}
    </span>
  );
}

export function Dot({ tone, className }: { tone: Tone; className?: string }) {
  return <span aria-hidden className={cn("inline-block h-2 w-2 shrink-0 rounded-full", TONE[tone].dot, className)} />;
}

export function StatTile({
  label,
  value,
  sub,
  tone,
  info,
}: {
  label: string;
  value: React.ReactNode;
  sub?: React.ReactNode;
  tone?: Tone;
  info?: string;
}) {
  return (
    <div className="glass-card flex min-w-0 flex-col gap-1 rounded-lg border border-glass-strong bg-glass px-4 py-3">
      <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
        {tone ? <Dot tone={tone} /> : null}
        <span className="truncate">{label}</span>
        {info ? <InfoTip text={info} /> : null}
      </div>
      <div className="font-display text-2xl font-semibold tracking-tight tabular-nums text-foreground">{value}</div>
      {sub ? <div className="truncate text-xs text-muted-foreground">{sub}</div> : null}
    </div>
  );
}

export function EmptyState({ icon, title, body }: { icon?: React.ReactNode; title: string; body?: React.ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-10 text-center">
      {icon ? <div className="text-muted-foreground/60">{icon}</div> : null}
      <p className="text-sm font-medium text-foreground">{title}</p>
      {body ? <div className="max-w-md text-xs leading-relaxed text-muted-foreground">{body}</div> : null}
    </div>
  );
}

export function SqlBlock({ sql, maxHeight = "12rem" }: { sql: string; maxHeight?: string }) {
  return (
    <div className="relative rounded-md border border-glass bg-glass-strong">
      <CopyButton value={sql} label="Copy SQL" className="absolute right-1.5 top-1.5" />
      <pre
        className="overflow-auto whitespace-pre-wrap break-words px-3 py-2 pr-9 font-mono text-[11.5px] leading-relaxed text-foreground/90"
        style={{ maxHeight }}
      >
        {sql}
      </pre>
    </div>
  );
}

export function Code({ children }: { children: React.ReactNode }) {
  return <code className="rounded bg-glass-strong px-1 py-0.5 font-mono text-[11.5px] text-foreground/90">{children}</code>;
}

/// Horizontal stacked bar: where a request's time goes. 2px surface gaps
/// between segments; the legend lives next to the table that uses it.
export function SplitBar({ parts, className }: { parts: { key: string; value: number; color: string; label: string }[]; className?: string }) {
  const total = parts.reduce((n, p) => n + Math.max(0, p.value), 0);
  if (total <= 0) return <div className={cn("h-2 rounded-full bg-glass-strong", className)} />;
  return (
    <div className={cn("flex h-2 w-full gap-[2px] overflow-hidden rounded-full", className)} role="img"
      aria-label={parts.map((p) => `${p.label} ${Math.round((100 * p.value) / total)}%`).join(", ")}>
      {parts.filter((p) => p.value > 0).map((p) => (
        <span key={p.key} className="h-full first:rounded-l-full last:rounded-r-full" title={`${p.label}: ${Math.round((100 * p.value) / total)}%`}
          style={{ width: `${(100 * p.value) / total}%`, backgroundColor: p.color, minWidth: 2 }} />
      ))}
    </div>
  );
}

export function Legend({ items }: { items: { label: string; color: string }[] }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
      {items.map((i) => (
        <span key={i.label} className="inline-flex items-center gap-1.5">
          <span aria-hidden className="h-2 w-2 rounded-sm" style={{ backgroundColor: i.color }} />
          {i.label}
        </span>
      ))}
    </div>
  );
}

export function ChartTooltipBox({ title, rows }: { title: string; rows: { label: string; value: string; color?: string }[] }) {
  return (
    <div className="min-w-40 rounded-md border border-glass-strong bg-popover px-3 py-2 text-xs shadow-lg">
      <div className="mb-1 font-medium text-foreground">{title}</div>
      <div className="flex flex-col gap-0.5">
        {rows.map((r) => (
          <div key={r.label} className="flex items-center justify-between gap-4">
            <span className="inline-flex items-center gap-1.5 text-muted-foreground">
              {r.color ? <span aria-hidden className="h-2 w-2 rounded-sm" style={{ backgroundColor: r.color }} /> : null}
              {r.label}
            </span>
            <span className="tabular-nums text-foreground">{r.value}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

// ------------------------------------------------------------- data table

export type Column<T> = {
  key: string;
  header: string;
  info?: string;
  align?: "left" | "right";
  render: (row: T) => React.ReactNode;
  sortValue?: (row: T) => number | string;
  className?: string;
};

/// Sortable, optionally searchable table for the metric lists. Rows are
/// small aggregates (hundreds at most), so sorting/filtering stays client-side.
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  initialSort,
  onRowClick,
  searchText,
  searchPlaceholder = "Filter…",
  maxRows = 200,
  empty,
  dense,
}: {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  initialSort?: { key: string; dir: "asc" | "desc" };
  onRowClick?: (row: T) => void;
  searchText?: (row: T) => string;
  searchPlaceholder?: string;
  maxRows?: number;
  empty?: React.ReactNode;
  dense?: boolean;
}) {
  const [sort, setSort] = React.useState(initialSort);
  const [filter, setFilter] = React.useState("");

  const sorted = React.useMemo(() => {
    let list = rows;
    if (searchText && filter.trim()) {
      const needle = filter.trim().toLowerCase();
      list = list.filter((r) => searchText(r).toLowerCase().includes(needle));
    }
    const col = columns.find((c) => c.key === sort?.key);
    if (!col?.sortValue || !sort) return list;
    const dir = sort.dir === "asc" ? 1 : -1;
    return [...list].sort((a, b) => {
      const av = col.sortValue!(a);
      const bv = col.sortValue!(b);
      if (typeof av === "number" && typeof bv === "number") return (av - bv) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
  }, [rows, columns, sort, filter, searchText]);

  const visible = sorted.slice(0, maxRows);

  return (
    <div className="flex flex-col gap-2">
      {searchText ? (
        <label className="relative block w-full max-w-xs">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder={searchPlaceholder}
            className="h-8 w-full rounded-md border border-glass bg-glass pl-8 pr-2 text-xs text-foreground outline-hidden placeholder:text-muted-foreground focus:border-primary/60"
          />
        </label>
      ) : null}
      <div className="overflow-x-auto rounded-md border border-glass">
        <table className="w-full border-collapse text-xs">
          <thead>
            <tr className="border-b border-glass bg-glass-strong/60 text-left text-muted-foreground">
              {columns.map((c) => {
                const active = sort?.key === c.key;
                return (
                  <th key={c.key} scope="col"
                    className={cn("whitespace-nowrap px-3 py-2 font-medium", c.align === "right" && "text-right", c.className)}>
                    <span className={cn("inline-flex items-center gap-1", c.align === "right" && "flex-row-reverse")}>
                      {c.sortValue ? (
                        <button
                          type="button"
                          onClick={() => setSort({ key: c.key, dir: active && sort?.dir === "desc" ? "asc" : "desc" })}
                          className={cn("inline-flex items-center gap-1 hover:text-foreground", active && "text-foreground")}
                        >
                          {c.header}
                          {active ? (sort?.dir === "desc" ? <ArrowDown className="h-3 w-3" /> : <ArrowUp className="h-3 w-3" />) : null}
                        </button>
                      ) : (
                        c.header
                      )}
                      {c.info ? <InfoTip text={c.info} /> : null}
                    </span>
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {visible.length === 0 ? (
              <tr>
                <td colSpan={columns.length} className="px-3 py-6 text-center text-muted-foreground">
                  {empty ?? "No data in this period."}
                </td>
              </tr>
            ) : (
              visible.map((row) => (
                <tr
                  key={rowKey(row)}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  className={cn(
                    "border-b border-glass last:border-b-0",
                    onRowClick && "cursor-pointer transition-colors hover:bg-glass-hover",
                  )}
                >
                  {columns.map((c) => (
                    <td key={c.key}
                      className={cn("px-3 align-top text-foreground", dense ? "py-1.5" : "py-2", c.align === "right" && "whitespace-nowrap text-right tabular-nums", c.className)}>
                      {c.render(row)}
                    </td>
                  ))}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
      {sorted.length > maxRows ? (
        <p className="text-[11px] text-muted-foreground">Showing {maxRows} of {sorted.length} rows.</p>
      ) : null}
    </div>
  );
}

/// Section switcher used inside a tab (e.g. Database → Queries / Tables / …).
export function SubTabs<K extends string>({
  value,
  onChange,
  items,
}: {
  value: K;
  onChange: (k: K) => void;
  items: { key: K; label: string; badge?: React.ReactNode }[];
}) {
  return (
    <div role="tablist" className="sd-segmented inline-flex flex-wrap rounded-md border border-glass bg-glass p-0.5">
      {items.map((i) => (
        <button
          key={i.key}
          type="button"
          role="tab"
          aria-selected={value === i.key}
          onClick={() => onChange(i.key)}
          className={cn(
            "inline-flex h-7 items-center gap-1.5 rounded-[5px] px-3 text-xs font-medium transition-colors",
            value === i.key ? "bg-background text-foreground shadow-xs" : "text-muted-foreground hover:text-foreground",
          )}
        >
          {i.label}
          {i.badge}
        </button>
      ))}
    </div>
  );
}
