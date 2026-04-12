import {
  useReactTable,
  getCoreRowModel,
  flexRender,
  createColumnHelper,
} from "@tanstack/react-table";
import { useColumnPrefsStore } from "@/stores/useColumnPrefsStore";
import { Skeleton } from "@/components/ui/skeleton";
import type { TicketListItem } from "@/lib/ticket-api";

function relativeTime(utc: string): string {
  const diff = Date.now() - new Date(utc).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function formatDate(utc: string): string {
  return new Date(utc).toISOString().replace("T", " ").slice(0, 16);
}

type ColoredBadgeProps = {
  label: string;
  color: string;
};

function ColoredBadge({ label, color }: ColoredBadgeProps) {
  return (
    <span
      className="inline-flex items-center rounded px-2 py-0.5 text-xs font-medium"
      style={{ backgroundColor: `${color}20`, color }}
    >
      {label}
    </span>
  );
}

const columnHelper = createColumnHelper<TicketListItem>();

const ALL_COLUMNS = [
  columnHelper.accessor("number", {
    id: "number",
    header: "#",
    cell: (info) => (
      <span className="font-mono text-primary">#{info.getValue()}</span>
    ),
  }),
  columnHelper.accessor("subject", {
    id: "subject",
    header: "Subject",
    cell: (info) => {
      const val = info.getValue();
      return (
        <span title={val} className="block max-w-[360px] truncate">
          {val.length > 60 ? `${val.slice(0, 60)}…` : val}
        </span>
      );
    },
  }),
  columnHelper.display({
    id: "requester",
    header: "Requester",
    cell: (info) => {
      const row = info.row.original;
      const name = [row.requesterFirstName, row.requesterLastName]
        .filter(Boolean)
        .join(" ");
      return (
        <span className="text-foreground/90">
          {name || row.requesterEmail}
        </span>
      );
    },
  }),
  columnHelper.accessor("companyName", {
    id: "companyName",
    header: "Company",
    cell: (info) => (
      <span className="text-muted-foreground">{info.getValue() ?? "—"}</span>
    ),
  }),
  columnHelper.accessor("queueName", {
    id: "queueName",
    header: "Queue",
    cell: (info) => {
      const row = info.row.original;
      // queueId is available but we don't have queue colors here without
      // loading taxonomy. Use a muted generic badge style for now.
      return (
        <span className="inline-flex items-center rounded px-2 py-0.5 text-xs font-medium bg-white/[0.07] text-foreground/80">
          {row.queueName}
        </span>
      );
    },
  }),
  columnHelper.accessor("statusName", {
    id: "statusName",
    header: "Status",
    cell: (info) => {
      const row = info.row.original;
      const catColors: Record<string, string> = {
        New: "#a78bfa",
        Open: "#60a5fa",
        Pending: "#fbbf24",
        Resolved: "#34d399",
        Closed: "#6b7280",
      };
      const color = catColors[row.statusStateCategory] ?? "#6b7280";
      return <ColoredBadge label={row.statusName} color={color} />;
    },
  }),
  columnHelper.accessor("priorityName", {
    id: "priorityName",
    header: "Priority",
    cell: (info) => {
      const row = info.row.original;
      const levelColors: Record<number, string> = {
        1: "#ef4444",
        2: "#f97316",
        3: "#eab308",
        4: "#6b7280",
      };
      const color = levelColors[row.priorityLevel] ?? "#6b7280";
      return <ColoredBadge label={row.priorityName} color={color} />;
    },
  }),
  columnHelper.display({
    id: "categoryName",
    header: "Category",
    cell: () => <span className="text-muted-foreground">—</span>,
  }),
  columnHelper.accessor("assigneeEmail", {
    id: "assigneeEmail",
    header: "Assignee",
    cell: (info) => {
      const val = info.getValue();
      return val ? (
        <span className="text-foreground/90">{val}</span>
      ) : (
        <span className="text-muted-foreground/60 italic">Unassigned</span>
      );
    },
  }),
  columnHelper.accessor("createdUtc", {
    id: "createdUtc",
    header: "Created",
    cell: (info) => (
      <span className="text-muted-foreground text-xs">{formatDate(info.getValue())}</span>
    ),
  }),
  columnHelper.accessor("updatedUtc", {
    id: "updatedUtc",
    header: "Updated",
    cell: (info) => (
      <span className="text-muted-foreground text-xs">{relativeTime(info.getValue())}</span>
    ),
  }),
  columnHelper.accessor("dueUtc", {
    id: "dueUtc",
    header: "Due",
    cell: (info) => {
      const val = info.getValue();
      if (!val) return <span className="text-muted-foreground/60">—</span>;
      const isPast = new Date(val).getTime() < Date.now();
      return (
        <span className={isPast ? "text-red-400 text-xs" : "text-muted-foreground text-xs"}>
          {formatDate(val)}
        </span>
      );
    },
  }),
];

type TicketTableProps = {
  data: TicketListItem[];
  onRowClick: (id: string) => void;
};

export function TicketTable({ data, onRowClick }: TicketTableProps) {
  const { visibleColumns } = useColumnPrefsStore();

  const columns = ALL_COLUMNS.filter((col) => visibleColumns.includes(col.id!));

  const table = useReactTable({
    data,
    columns,
    getCoreRowModel: getCoreRowModel(),
  });

  return (
    <div className="glass-card overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead className="bg-white/[0.03] sticky top-0 z-10">
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
            {table.getRowModel().rows.map((row) => (
              <tr
                key={row.id}
                className="border-b border-white/5 hover:bg-white/[0.04] cursor-pointer transition-colors"
                onClick={() => onRowClick(row.original.id)}
              >
                {row.getVisibleCells().map((cell) => (
                  <td key={cell.id} className="px-4 py-3 text-sm">
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export function TicketTableSkeleton() {
  return (
    <div className="glass-card overflow-hidden">
      <div className="p-4 space-y-2">
        {Array.from({ length: 6 }).map((_, i) => (
          <Skeleton key={i} className="h-10 w-full" />
        ))}
      </div>
    </div>
  );
}
