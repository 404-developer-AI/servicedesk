import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import {
  useReactTable,
  getCoreRowModel,
  flexRender,
} from "@tanstack/react-table";
import { ChevronDown } from "lucide-react";
import { ALL_COLUMNS } from "./TicketTable";
import { taxonomyApi, settingsApi, type TicketGroupingSettings } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useColumnPrefsStore } from "@/stores/useColumnPrefsStore";
import { useTheme } from "@/app/ThemeProvider";
import type { TicketListItem, DisplayConfig } from "@/lib/ticket-api";
import type { CSSProperties } from "react";
import { colorPillStyle } from "@/lib/colorPill";

// ---- Group key helpers ----

type GroupByField =
  | "statusId"
  | "priorityId"
  | "queueId"
  | "assigneeUserId"
  | "categoryId"
  | "requesterContactId"
  | "companyName";

const GROUP_BY_FIELD_MAP: Record<
  GroupByField,
  { idKey: keyof TicketListItem; labelKey: keyof TicketListItem; colorKey?: keyof TicketListItem }
> = {
  statusId: { idKey: "statusId", labelKey: "statusName", colorKey: "statusColor" },
  priorityId: { idKey: "priorityId", labelKey: "priorityName", colorKey: "priorityColor" },
  queueId: { idKey: "queueId", labelKey: "queueName" },
  assigneeUserId: { idKey: "assigneeUserId", labelKey: "assigneeEmail" },
  categoryId: { idKey: "categoryId", labelKey: "categoryName" },
  requesterContactId: { idKey: "requesterContactId", labelKey: "requesterEmail" },
  companyName: { idKey: "companyName", labelKey: "companyName" },
};

type TicketGroup = {
  key: string;
  label: string;
  color?: string;
  items: TicketListItem[];
};

// ---- Per-group (per-state) sorting ----

// A high-priority ticket only floats while it is still New or Open; once it is
// Pending (or Resolved/Closed) it drops back into the normal list.
function isFloatable(t: TicketListItem): boolean {
  return (
    !t.priorityIsDefault &&
    (t.statusStateCategory === "New" || t.statusStateCategory === "Open")
  );
}

// Numeric sort keys for the per-state group sort. Timestamps become epoch ms;
// a missing value yields NaN and is always pushed to the end of the group.
const GROUP_SORT_ACCESSORS: Record<string, (t: TicketListItem) => number> = {
  updatedUtc: (t) => Date.parse(t.updatedUtc),
  createdUtc: (t) => Date.parse(t.createdUtc),
  pendingTillUtc: (t) => (t.pendingTillUtc ? Date.parse(t.pendingTillUtc) : NaN),
  dueUtc: (t) => (t.dueUtc ? Date.parse(t.dueUtc) : NaN),
  priorityLevel: (t) => t.priorityLevel,
  number: (t) => t.number,
};

function sortGroupItems(
  items: TicketListItem[],
  field: string,
  direction: "asc" | "desc",
): TicketListItem[] {
  const accessor = GROUP_SORT_ACCESSORS[field];
  if (!accessor) return items;
  const dir = direction === "desc" ? -1 : 1;
  return [...items].sort((a, b) => {
    const av = accessor(a);
    const bv = accessor(b);
    const aNan = Number.isNaN(av);
    const bNan = Number.isNaN(bv);
    if (aNan && bNan) return 0;
    if (aNan) return 1; // nulls/blanks last regardless of direction
    if (bNan) return -1;
    return (av - bv) * dir;
  });
}

// Apply the configured per-state sort to a status group. Pending groups use the
// Pending rule; New/Open groups use the Open/New rule; everything else keeps the
// incoming (global view) order.
function applyGroupSort(
  group: TicketGroup,
  cfg: TicketGroupingSettings,
): TicketGroup {
  const category = group.items[0]?.statusStateCategory;
  if (category === "Pending") {
    return { ...group, items: sortGroupItems(group.items, cfg.pendingField, cfg.pendingDirection) };
  }
  if (category === "New" || category === "Open") {
    return { ...group, items: sortGroupItems(group.items, cfg.openNewField, cfg.openNewDirection) };
  }
  return group;
}

function groupTickets(
  items: TicketListItem[],
  groupBy: GroupByField,
): TicketGroup[] {
  const mapping = GROUP_BY_FIELD_MAP[groupBy];
  if (!mapping) return [{ key: "__all__", label: "All", items }];

  const groups = new Map<string, TicketGroup>();
  for (const item of items) {
    const id = String(item[mapping.idKey] ?? "__null__");
    const label = String(item[mapping.labelKey] ?? "Unassigned");
    const color = mapping.colorKey ? (item[mapping.colorKey] as string | null) ?? undefined : undefined;
    if (!groups.has(id)) {
      groups.set(id, { key: id, label, color, items: [] });
    }
    groups.get(id)!.items.push(item);
  }
  return Array.from(groups.values());
}

function orderGroups(
  groups: TicketGroup[],
  groupOrder: string[] | null | undefined,
  taxonomySortMap: Map<string, number> | undefined,
): TicketGroup[] {
  if (groupOrder && groupOrder.length > 0) {
    const orderIndex = new Map(groupOrder.map((id, i) => [id, i]));
    return [...groups].sort((a, b) => {
      const ai = orderIndex.get(a.key) ?? 99999;
      const bi = orderIndex.get(b.key) ?? 99999;
      if (ai !== bi) return ai - bi;
      const ta = taxonomySortMap?.get(a.key) ?? 99999;
      const tb = taxonomySortMap?.get(b.key) ?? 99999;
      return ta - tb;
    });
  }
  if (taxonomySortMap) {
    return [...groups].sort((a, b) => {
      const ta = taxonomySortMap.get(a.key) ?? 99999;
      const tb = taxonomySortMap.get(b.key) ?? 99999;
      if (ta !== tb) return ta - tb;
      return a.label.localeCompare(b.label);
    });
  }
  return [...groups].sort((a, b) => a.label.localeCompare(b.label));
}

// ---- Taxonomy sort order hook ----

function useTaxonomySortMap(groupBy: string | null | undefined): Map<string, number> | undefined {
  const isStatus = groupBy === "statusId";
  const isPriority = groupBy === "priorityId";
  const isQueue = groupBy === "queueId";
  const isCategory = groupBy === "categoryId";

  const { data: statuses } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
    enabled: isStatus,
    staleTime: 60_000,
  });
  const { data: priorities } = useQuery({
    queryKey: ["taxonomy", "priorities"],
    queryFn: () => taxonomyApi.priorities.list(),
    enabled: isPriority,
    staleTime: 60_000,
  });
  const { data: queues } = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
    enabled: isQueue,
    staleTime: 60_000,
  });
  const { data: categories } = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: () => taxonomyApi.categories.list(),
    enabled: isCategory,
    staleTime: 60_000,
  });

  return React.useMemo(() => {
    const items = isStatus ? statuses : isPriority ? priorities : isQueue ? queues : isCategory ? categories : undefined;
    if (!items) return undefined;
    const map = new Map<string, number>();
    items.forEach((item: { id: string; sortOrder: number }) => {
      map.set(item.id, item.sortOrder);
    });
    return map;
  }, [isStatus, isPriority, isQueue, isCategory, statuses, priorities, queues, categories]);
}

// ---- Main component ----

/// v0.0.102 — row selection for bulk actions. The page owns the selected
/// set; the list owns the shift-click range anchor because only the list
/// knows the displayed order (groups, float, per-group sort).
export type TicketSelection = {
  selected: Set<string>;
  onToggle: (id: string) => void;
  onSetMany: (ids: string[], checked: boolean) => void;
};

type GroupedTicketListProps = {
  items: TicketListItem[];
  displayConfig: DisplayConfig;
  onRowClick: (id: string) => void;
  footer?: React.ReactNode;
  selection?: TicketSelection;
};

const CHECKBOX_CLASS =
  "h-3.5 w-3.5 cursor-pointer rounded border border-glass-strong bg-glass accent-primary";

/// Native checkbox that also renders the indeterminate state (only settable
/// through the DOM property, not an attribute).
function TriStateCheckbox({
  checked,
  indeterminate,
  onClick,
  ariaLabel,
}: {
  checked: boolean;
  indeterminate?: boolean;
  onClick: (e: React.MouseEvent<HTMLInputElement>) => void;
  ariaLabel: string;
}) {
  const ref = React.useRef<HTMLInputElement>(null);
  React.useEffect(() => {
    if (ref.current) ref.current.indeterminate = !!indeterminate && !checked;
  }, [indeterminate, checked]);
  return (
    <input
      ref={ref}
      type="checkbox"
      checked={checked}
      // Selection logic lives in onClick so we can read shiftKey for range
      // selection; onChange is a no-op to keep the input controlled.
      onChange={() => {}}
      onMouseDown={(e) => {
        // Stop shift-click from extending a native text selection.
        if (e.shiftKey) e.preventDefault();
      }}
      onClick={(e) => {
        e.stopPropagation();
        onClick(e);
      }}
      aria-label={ariaLabel}
      className={CHECKBOX_CLASS}
    />
  );
}

export function GroupedTicketList({
  items,
  displayConfig,
  onRowClick,
  footer,
  selection,
}: GroupedTicketListProps) {
  const theme = useTheme();
  const [collapsedGroups, setCollapsedGroups] = React.useState<Set<string>>(
    new Set(),
  );
  // Anchor for shift-click range selection: the id of the last plain-clicked
  // row checkbox. Ref, not state — it never needs to re-render anything.
  const anchorRef = React.useRef<string | null>(null);

  const groupBy = displayConfig.groupBy as GroupByField | null | undefined;
  const hasGrouping = !!groupBy && groupBy in GROUP_BY_FIELD_MAP;
  const hasPriorityFloat = !!displayConfig.priorityFloat;
  const taxonomySortMap = useTaxonomySortMap(groupBy);
  const { visibleColumns } = useColumnPrefsStore();

  // Per-state group sort config (admin-tunable). Only consumed when grouping by
  // Status; falls open to the global view sort if absent or disabled.
  const { data: groupingCfg } = useQuery({
    queryKey: ["settings", "ticket-grouping"],
    queryFn: () => settingsApi.ticketGrouping(),
    staleTime: 5 * 60_000,
  });

  const columns = React.useMemo(
    () => ALL_COLUMNS.filter((col) => visibleColumns.includes(col.id!)),
    [visibleColumns],
  );

  const table = useReactTable({
    data: items,
    columns,
    getCoreRowModel: getCoreRowModel(),
  });

  // Build ordered groups
  const orderedGroups = React.useMemo(() => {
    if (!hasGrouping && !hasPriorityFloat) {
      return [{ key: "__all__", label: "", color: undefined, items }] as TicketGroup[];
    }

    let floatItems: TicketListItem[] = [];
    let normalItems: TicketListItem[] = items;

    if (hasPriorityFloat) {
      floatItems = items.filter(isFloatable);
      normalItems = items.filter((t) => !isFloatable(t));
    }

    const result: TicketGroup[] = [];

    if (floatItems.length > 0) {
      result.push({ key: "__float__", label: "Priority", color: "#ef4444", items: floatItems });
    }

    if (hasGrouping) {
      const raw = groupTickets(normalItems, groupBy!);
      let ordered = orderGroups(raw, displayConfig.groupOrder, taxonomySortMap);
      // Per-state sort only when grouping by Status and the admin toggle is on.
      if (groupBy === "statusId" && groupingCfg?.enabled) {
        ordered = ordered.map((g) => applyGroupSort(g, groupingCfg));
      }
      result.push(...ordered);
    } else if (normalItems.length > 0) {
      result.push({ key: "__all__", label: "All tickets", color: undefined, items: normalItems });
    }

    return result;
  }, [items, hasGrouping, hasPriorityFloat, groupBy, displayConfig.groupOrder, taxonomySortMap, groupingCfg]);

  const showGroupHeaders = hasGrouping || hasPriorityFloat;

  // Displayed order across all groups (collapsed groups included, so a
  // shift-range spanning a collapsed group still selects it — that matches
  // what "from here to there" means visually in the header rows).
  const displayedIds = React.useMemo(
    () => orderedGroups.flatMap((g) => g.items.map((t) => t.id)),
    [orderedGroups],
  );

  // A plain click toggles the row and moves the anchor; a shift-click selects
  // the whole range from the anchor to the clicked row (additive), following
  // the displayed order so it respects grouping + sort. Same semantics as
  // the timesheet back-office tabs.
  const handleRowCheck = (id: string, shift: boolean) => {
    if (!selection) return;
    if (shift && anchorRef.current !== null) {
      const a = displayedIds.indexOf(anchorRef.current);
      const b = displayedIds.indexOf(id);
      if (a >= 0 && b >= 0) {
        const [lo, hi] = a < b ? [a, b] : [b, a];
        selection.onSetMany(displayedIds.slice(lo, hi + 1), true);
        return;
      }
    }
    selection.onToggle(id);
    anchorRef.current = id;
  };

  const allSelected = !!selection && items.length > 0 && items.every((t) => selection.selected.has(t.id));
  const anySelected = !!selection && items.some((t) => selection.selected.has(t.id));

  // Build a lookup from item id to react-table row for rendering
  const rowById = React.useMemo(() => {
    const map = new Map<string, (typeof table extends { getRowModel: () => { rows: (infer R)[] } } ? R : never)>();
    for (const row of table.getRowModel().rows) {
      map.set(row.original.id, row);
    }
    return map;
  }, [table.getRowModel().rows]);

  function toggleCollapse(key: string) {
    setCollapsedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  return (
    <div className="glass-card h-full overflow-auto">
        <table className="w-full text-left text-sm">
          <thead className="sd-table-head sticky top-0 z-10 bg-[hsl(256deg_28.3%_89.61%)] dark:bg-[hsl(240_10%_8%)]">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id}>
                {selection && (
                  <th className="w-10 px-3 py-3 border-b border-glass">
                    <TriStateCheckbox
                      checked={allSelected}
                      indeterminate={anySelected}
                      ariaLabel={allSelected ? "Deselect all tickets" : "Select all tickets"}
                      onClick={() => {
                        selection.onSetMany(items.map((t) => t.id), !allSelected);
                        anchorRef.current = null;
                      }}
                    />
                  </th>
                )}
                {headerGroup.headers.map((header) => (
                  <th
                    key={header.id}
                    className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wider text-muted-foreground border-b border-glass"
                  >
                    {header.isPlaceholder
                      ? null
                      : flexRender(header.column.columnDef.header, header.getContext())}
                  </th>
                ))}
              </tr>
            ))}
          </thead>
          <tbody>
            {orderedGroups.map((group) => {
              const isCollapsed = collapsedGroups.has(group.key);
              const color = group.color ?? "#6b7280";
              const groupAllSelected =
                !!selection && group.items.length > 0 && group.items.every((t) => selection.selected.has(t.id));
              const groupAnySelected =
                !!selection && group.items.some((t) => selection.selected.has(t.id));

              return (
                <React.Fragment key={group.key}>
                  {showGroupHeaders && (
                    <tr
                      className="border-b border-glass-strong bg-glass hover:bg-glass-hover transition-colors cursor-pointer"
                      onClick={() => toggleCollapse(group.key)}
                    >
                      {selection && (
                        <td className="w-10 px-3 py-2">
                          <TriStateCheckbox
                            checked={groupAllSelected}
                            indeterminate={groupAnySelected}
                            ariaLabel={`${groupAllSelected ? "Deselect" : "Select"} all tickets in ${group.label || "this group"}`}
                            onClick={() => {
                              selection.onSetMany(group.items.map((t) => t.id), !groupAllSelected);
                              anchorRef.current = null;
                            }}
                          />
                        </td>
                      )}
                      <td colSpan={columns.length} className="px-4 py-2">
                        <div className="flex items-center gap-3">
                          <ChevronDown
                            className={cn(
                              "h-3.5 w-3.5 text-muted-foreground transition-transform duration-150",
                              isCollapsed && "-rotate-90",
                            )}
                          />
                          <span
                            className="inline-flex items-center rounded px-2 py-0.5 text-xs font-medium"
                            style={colorPillStyle(color, theme)}
                          >
                            {group.label}
                          </span>
                          <span className="text-[11px] text-muted-foreground/60">
                            {group.items.length} ticket{group.items.length !== 1 ? "s" : ""}
                          </span>
                        </div>
                      </td>
                    </tr>
                  )}
                  {!isCollapsed &&
                    group.items.map((item) => {
                      const row = rowById.get(item.id);
                      if (!row) return null;

                      const pColor = item.priorityColor || "#6b7280";
                      const accent = !item.priorityIsDefault && item.priorityColor;
                      const rowStyle: CSSProperties = {
                        boxShadow: `inset 3px 0 0 0 ${pColor}`,
                        ...(accent
                          ? {
                              backgroundImage: `linear-gradient(to right, ${pColor}12 0%, ${pColor}06 30%, transparent 60%)`,
                            }
                          : {}),
                      };

                      const isSelected = !!selection && selection.selected.has(item.id);

                      return (
                        <tr
                          key={row.id}
                          className={cn(
                            "border-b border-glass hover:bg-glass-hover cursor-pointer transition-colors",
                            isSelected && "bg-primary/[0.07] hover:bg-primary/[0.1]",
                          )}
                          style={rowStyle}
                          onClick={() => onRowClick(item.id)}
                          aria-selected={selection ? isSelected : undefined}
                        >
                          {selection && (
                            <td className="w-10 px-3 py-3">
                              <TriStateCheckbox
                                checked={isSelected}
                                ariaLabel={`Select ticket #${item.number}`}
                                onClick={(e) => handleRowCheck(item.id, e.shiftKey)}
                              />
                            </td>
                          )}
                          {row.getVisibleCells().map((cell) => (
                            <td key={cell.id} className="px-4 py-3 text-sm">
                              {flexRender(cell.column.columnDef.cell, cell.getContext())}
                            </td>
                          ))}
                        </tr>
                      );
                    })}
                </React.Fragment>
              );
            })}
          </tbody>
        </table>
        {footer}
    </div>
  );
}
