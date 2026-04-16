import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import {
  useReactTable,
  getCoreRowModel,
  flexRender,
} from "@tanstack/react-table";
import { ChevronDown } from "lucide-react";
import { ALL_COLUMNS } from "./TicketTable";
import { taxonomyApi } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useColumnPrefsStore } from "@/stores/useColumnPrefsStore";
import type { TicketListItem, DisplayConfig } from "@/lib/ticket-api";
import type { CSSProperties } from "react";

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

type GroupedTicketListProps = {
  items: TicketListItem[];
  displayConfig: DisplayConfig;
  onRowClick: (id: string) => void;
  footer?: React.ReactNode;
};

export function GroupedTicketList({
  items,
  displayConfig,
  onRowClick,
  footer,
}: GroupedTicketListProps) {
  const [collapsedGroups, setCollapsedGroups] = React.useState<Set<string>>(
    new Set(),
  );

  const groupBy = displayConfig.groupBy as GroupByField | null | undefined;
  const hasGrouping = !!groupBy && groupBy in GROUP_BY_FIELD_MAP;
  const hasPriorityFloat = !!displayConfig.priorityFloat;
  const taxonomySortMap = useTaxonomySortMap(groupBy);
  const { visibleColumns } = useColumnPrefsStore();

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
      floatItems = items.filter((t) => !t.priorityIsDefault);
      normalItems = items.filter((t) => t.priorityIsDefault);
    }

    const result: TicketGroup[] = [];

    if (floatItems.length > 0) {
      result.push({ key: "__float__", label: "Priority", color: "#ef4444", items: floatItems });
    }

    if (hasGrouping) {
      const raw = groupTickets(normalItems, groupBy!);
      const ordered = orderGroups(raw, displayConfig.groupOrder, taxonomySortMap);
      result.push(...ordered);
    } else if (normalItems.length > 0) {
      result.push({ key: "__all__", label: "All tickets", color: undefined, items: normalItems });
    }

    return result;
  }, [items, hasGrouping, hasPriorityFloat, groupBy, displayConfig.groupOrder, taxonomySortMap]);

  const showGroupHeaders = hasGrouping || hasPriorityFloat;
  const colCount = columns.length;

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
          <thead className="sticky top-0 z-10 bg-[hsl(245_14%_12%)]">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id}>
                {headerGroup.headers.map((header) => (
                  <th
                    key={header.id}
                    className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wider text-muted-foreground border-b border-white/10"
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

              return (
                <React.Fragment key={group.key}>
                  {showGroupHeaders && (
                    <tr
                      className="border-b border-white/[0.06] bg-white/[0.02] hover:bg-white/[0.04] transition-colors cursor-pointer"
                      onClick={() => toggleCollapse(group.key)}
                    >
                      <td colSpan={colCount} className="px-4 py-2">
                        <div className="flex items-center gap-3">
                          <ChevronDown
                            className={cn(
                              "h-3.5 w-3.5 text-muted-foreground transition-transform duration-150",
                              isCollapsed && "-rotate-90",
                            )}
                          />
                          <span
                            className="inline-flex items-center rounded px-2 py-0.5 text-xs font-medium"
                            style={{ backgroundColor: `${color}20`, color }}
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

                      return (
                        <tr
                          key={row.id}
                          className="border-b border-white/5 hover:bg-white/[0.04] cursor-pointer transition-colors"
                          style={rowStyle}
                          onClick={() => onRowClick(item.id)}
                        >
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
