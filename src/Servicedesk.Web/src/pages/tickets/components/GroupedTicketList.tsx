import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown } from "lucide-react";
import { TicketTable } from "./TicketTable";
import { taxonomyApi } from "@/lib/api";
import { cn } from "@/lib/utils";
import type { TicketListItem, DisplayConfig } from "@/lib/ticket-api";

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
      // Fall back to taxonomy sort for items not in the override
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

// ---- Collapsible group header ----

function GroupHeader({
  group,
  isCollapsed,
  onToggle,
}: {
  group: TicketGroup;
  isCollapsed: boolean;
  onToggle: () => void;
}) {
  const color = group.color ?? "#6b7280";

  return (
    <button
      type="button"
      onClick={onToggle}
      className="flex w-full items-center gap-3 px-4 py-2 rounded-t-lg bg-white/[0.03] border border-white/[0.06] border-b-0 transition-colors hover:bg-white/[0.05] group"
    >
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
    </button>
  );
}

// ---- Main component ----

type GroupedTicketListProps = {
  items: TicketListItem[];
  displayConfig: DisplayConfig;
  onRowClick: (id: string) => void;
};

export function GroupedTicketList({
  items,
  displayConfig,
  onRowClick,
}: GroupedTicketListProps) {
  const [collapsedGroups, setCollapsedGroups] = React.useState<Set<string>>(
    new Set(),
  );

  const groupBy = displayConfig.groupBy as GroupByField | null | undefined;
  const hasGrouping = !!groupBy && groupBy in GROUP_BY_FIELD_MAP;
  const hasPriorityFloat = !!displayConfig.priorityFloat;
  const taxonomySortMap = useTaxonomySortMap(groupBy);

  // No grouping and no float — render flat table
  if (!hasGrouping && !hasPriorityFloat) {
    return <TicketTable data={items} onRowClick={onRowClick} />;
  }

  // Split float vs normal
  let floatItems: TicketListItem[] = [];
  let normalItems: TicketListItem[] = items;

  if (hasPriorityFloat) {
    floatItems = items.filter((t) => !t.priorityIsDefault);
    normalItems = items.filter((t) => t.priorityIsDefault);
  }

  // Group normal items
  let normalGroups: TicketGroup[];
  if (hasGrouping) {
    const raw = groupTickets(normalItems, groupBy!);
    normalGroups = orderGroups(raw, displayConfig.groupOrder, taxonomySortMap);
  } else {
    normalGroups = normalItems.length > 0
      ? [{ key: "__all__", label: "All tickets", items: normalItems }]
      : [];
  }

  function toggleCollapse(key: string) {
    setCollapsedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  return (
    <div className="space-y-3">
      {/* Priority float section */}
      {floatItems.length > 0 && (
        <div>
          <GroupHeader
            group={{
              key: "__float__",
              label: "Priority",
              color: "#ef4444",
              items: floatItems,
            }}
            isCollapsed={collapsedGroups.has("__float__")}
            onToggle={() => toggleCollapse("__float__")}
          />
          {!collapsedGroups.has("__float__") && (
            <TicketTable data={floatItems} onRowClick={onRowClick} />
          )}
        </div>
      )}

      {/* Grouped sections */}
      {normalGroups.map((group) => (
        <div key={group.key}>
          {hasGrouping && (
            <GroupHeader
              group={group}
              isCollapsed={collapsedGroups.has(group.key)}
              onToggle={() => toggleCollapse(group.key)}
            />
          )}
          {!collapsedGroups.has(group.key) && (
            <TicketTable data={group.items} onRowClick={onRowClick} />
          )}
        </div>
      ))}
    </div>
  );
}
