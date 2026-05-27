import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Boxes, Loader2, Trash2 } from "lucide-react";
import {
  apiErrorMessage,
  zammadMappingApi,
  type ZammadMappingRow,
  type ZammadMappingTarget,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";

export const MAPPING_QK = ["integrations", "zammad", "mappings"] as const;

type Category = "groups" | "states" | "priorities";

const CATEGORY_LABELS: Record<Category, { title: string; subtitle: string }> = {
  groups: {
    title: "Group mapping",
    subtitle:
      "Each Zammad group maps to one local queue. Required before a dry-run can resolve a ticket's destination.",
  },
  states: {
    title: "State mapping",
    subtitle:
      "Each Zammad state maps to one local status. Required so imported tickets land in the right pipeline column.",
  },
  priorities: {
    title: "Priority mapping",
    subtitle:
      "Each Zammad priority maps to one local priority. Tickets with unmapped priorities are skipped during dry-run.",
  },
};

type Props = { ready: boolean };

/// Renders three mapping tables (groups / states / priorities). Each row
/// exposes a Select dropdown that fires an upsert mutation on change; a
/// trash button next to an existing mapping clears it. All three blocks
/// share a single overview query so a single PUT roundtrip refreshes
/// every counter pill at once.
export function ZammadMappingSection({ ready }: Props) {
  const qc = useQueryClient();

  const overview = useQuery({
    queryKey: MAPPING_QK,
    queryFn: zammadMappingApi.overview,
    enabled: ready,
    staleTime: 30_000,
  });

  function invalidate() {
    void qc.invalidateQueries({ queryKey: MAPPING_QK });
  }

  if (!ready) {
    return (
      <section className="space-y-2 rounded-xl border border-glass-strong bg-glass p-5">
        <SectionHeader />
        <div className="rounded-md border border-amber-400/20 bg-amber-500/[0.05] p-3 text-xs text-amber-200">
          Save a base URL + token and toggle <span className="font-mono">Zammad.Enabled</span>{" "}
          on first. Mappings load live from Zammad.
        </div>
      </section>
    );
  }

  if (overview.isLoading) {
    return (
      <section className="space-y-3 rounded-xl border border-glass-strong bg-glass p-5">
        <SectionHeader />
        <Skeleton className="h-16 w-full bg-glass" />
        <Skeleton className="h-16 w-full bg-glass" />
        <Skeleton className="h-16 w-full bg-glass" />
      </section>
    );
  }

  if (overview.isError) {
    return (
      <section className="space-y-3 rounded-xl border border-glass-strong bg-glass p-5">
        <SectionHeader />
        <div className="rounded-md border border-rose-400/30 bg-rose-500/[0.08] p-3 text-xs text-rose-200">
          Could not load mappings — {overview.error.message}
        </div>
      </section>
    );
  }

  const data = overview.data!;
  return (
    <section className="space-y-5 rounded-xl border border-glass-strong bg-glass p-5">
      <SectionHeader
        unmappedGroupCount={data.unmappedGroupCount}
        unmappedStateCount={data.unmappedStateCount}
        unmappedPriorityCount={data.unmappedPriorityCount}
      />
      <MappingBlock
        category="groups"
        rows={data.groups}
        targets={data.localQueues}
        onChange={invalidate}
      />
      <MappingBlock
        category="states"
        rows={data.states}
        targets={data.localStatuses}
        onChange={invalidate}
      />
      <MappingBlock
        category="priorities"
        rows={data.priorities}
        targets={data.localPriorities}
        onChange={invalidate}
      />
    </section>
  );
}

function SectionHeader(props?: {
  unmappedGroupCount?: number;
  unmappedStateCount?: number;
  unmappedPriorityCount?: number;
}) {
  const total =
    (props?.unmappedGroupCount ?? 0) +
    (props?.unmappedStateCount ?? 0) +
    (props?.unmappedPriorityCount ?? 0);
  return (
    <div className="flex items-start justify-between gap-3">
      <div>
        <div className="flex items-center gap-2 text-sm font-medium text-foreground">
          <Boxes className="h-4 w-4 text-muted-foreground" />
          Mapping
        </div>
        <p className="mt-1 text-xs text-muted-foreground/70">
          Decide how Zammad groups / states / priorities translate into your
          local queues / statuses / priorities. The dry-run uses this map to
          resolve each ticket; unmapped items are skipped + reported.
        </p>
      </div>
      {total > 0 ? (
        <Badge
          variant="outline"
          className="border-amber-400/30 bg-amber-500/[0.08] text-amber-200"
        >
          {total} unmapped
        </Badge>
      ) : (
        <Badge
          variant="outline"
          className="border-emerald-400/30 bg-emerald-500/[0.08] text-emerald-200"
        >
          All mapped
        </Badge>
      )}
    </div>
  );
}

function MappingBlock({
  category,
  rows,
  targets,
  onChange,
}: {
  category: Category;
  rows: ZammadMappingRow[];
  targets: ZammadMappingTarget[];
  onChange: () => void;
}) {
  const meta = CATEGORY_LABELS[category];
  return (
    <div className="space-y-2">
      <div className="flex items-baseline justify-between">
        <div className="text-xs font-medium uppercase tracking-wider text-foreground/80">
          {meta.title}
        </div>
        <div className="text-[10px] text-muted-foreground/60">
          {rows.length} {rows.length === 1 ? "row" : "rows"}
        </div>
      </div>
      <p className="text-[11px] text-muted-foreground/60">{meta.subtitle}</p>
      <div className="overflow-hidden rounded-md border border-glass-strong bg-glass">
        <table className="w-full text-xs">
          <thead className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
            <tr className="border-b border-glass">
              <th className="px-3 py-2 text-left">Zammad</th>
              <th className="px-3 py-2 text-left">Local target</th>
              <th className="px-3 py-2 text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr>
                <td
                  colSpan={3}
                  className="px-3 py-4 text-center text-muted-foreground/70"
                >
                  Zammad returned no {meta.title.toLowerCase()}.
                </td>
              </tr>
            ) : (
              rows.map((row) => (
                <MappingRow
                  key={row.zammadId}
                  category={category}
                  row={row}
                  targets={targets}
                  onChange={onChange}
                />
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function MappingRow({
  category,
  row,
  targets,
  onChange,
}: {
  category: Category;
  row: ZammadMappingRow;
  targets: ZammadMappingTarget[];
  onChange: () => void;
}) {
  const [pending, setPending] = useState(false);

  const putMutation = useMutation({
    mutationFn: (targetId: string) => {
      if (category === "groups")
        return zammadMappingApi.putGroup(row.zammadId, row.zammadName, targetId);
      if (category === "states")
        return zammadMappingApi.putState(row.zammadId, row.zammadName, targetId);
      return zammadMappingApi.putPriority(row.zammadId, row.zammadName, targetId);
    },
    onSuccess: () => {
      toast.success(`Mapping saved.`);
      onChange();
    },
    onError: (err) => toast.error(apiErrorMessage(err)),
    onSettled: () => setPending(false),
  });

  const deleteMutation = useMutation({
    mutationFn: () => {
      if (category === "groups") return zammadMappingApi.deleteGroup(row.zammadId);
      if (category === "states") return zammadMappingApi.deleteState(row.zammadId);
      return zammadMappingApi.deletePriority(row.zammadId);
    },
    onSuccess: () => {
      toast.success("Mapping cleared.");
      onChange();
    },
    onError: (err) => toast.error(apiErrorMessage(err)),
    onSettled: () => setPending(false),
  });

  function handleChange(value: string) {
    if (value === row.mappedToId) return;
    setPending(true);
    putMutation.mutate(value);
  }

  function handleDelete() {
    if (!row.mappedToId) return;
    setPending(true);
    deleteMutation.mutate();
  }

  return (
    <tr className="border-b border-glass last:border-b-0">
      <td className="px-3 py-1.5 align-middle">
        <div className="flex items-center gap-2">
          <span className="text-foreground">{row.zammadName}</span>
          {!row.zammadActive ? (
            <span className="rounded bg-glass px-1.5 py-0.5 text-[10px] uppercase text-muted-foreground/60">
              inactive
            </span>
          ) : null}
          <span className="font-mono text-[10px] text-muted-foreground/40">
            #{row.zammadId}
          </span>
        </div>
      </td>
      <td className="px-3 py-1.5 align-middle">
        <Select value={row.mappedToId ?? ""} onValueChange={handleChange}>
          <SelectTrigger
            className={cn(
              "h-8 w-full bg-glass text-xs",
              row.mappedToId
                ? ""
                : "border-amber-400/30 text-amber-200 hover:border-amber-300/40",
            )}
            disabled={pending}
          >
            <SelectValue placeholder="Needs mapping" />
          </SelectTrigger>
          <SelectContent>
            {targets.length === 0 ? (
              <SelectItem disabled value="__none">
                No local targets available
              </SelectItem>
            ) : (
              targets.map((t) => (
                <SelectItem key={t.id} value={t.id}>
                  {t.name}
                  {!t.isActive ? " (inactive)" : ""}
                </SelectItem>
              ))
            )}
          </SelectContent>
        </Select>
      </td>
      <td className="px-3 py-1.5 text-right align-middle">
        {row.mappedToId ? (
          <Button
            type="button"
            size="sm"
            variant="ghost"
            className="h-7 text-muted-foreground hover:text-rose-300"
            onClick={handleDelete}
            disabled={pending}
            aria-label="Clear mapping"
          >
            {pending && deleteMutation.isPending ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
            ) : (
              <Trash2 className="h-3.5 w-3.5" />
            )}
          </Button>
        ) : (
          <span className="text-[10px] text-muted-foreground/40">—</span>
        )}
      </td>
    </tr>
  );
}
