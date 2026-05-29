import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Link2, Link2Off, Search, Sparkles } from "lucide-react";
import { trmmAdminApi, type TrmmClientMapping } from "@/lib/api";
import { companyApi } from "@/lib/ticket-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

const MAPPINGS_QK = ["integrations", "trmm", "client-mappings"] as const;

export function TrmmClientMappingsSection() {
  const qc = useQueryClient();
  const mappings = useQuery({
    queryKey: MAPPINGS_QK,
    queryFn: () => trmmAdminApi.listClientMappings(),
  });
  const [filter, setFilter] = useState("");

  const filtered = useMemo(() => {
    const rows = mappings.data?.items ?? [];
    const q = filter.trim().toLowerCase();
    if (q.length === 0) return rows;
    return rows.filter(
      (r) =>
        r.name.toLowerCase().includes(q) ||
        (r.code ?? "").toLowerCase().includes(q) ||
        (r.companyName ?? "").toLowerCase().includes(q),
    );
  }, [mappings.data, filter]);

  return (
    <section className="rounded-lg border border-glass bg-glass p-5 space-y-4">
      <div className="flex items-center justify-between gap-4">
        <div>
          <h2 className="text-sm font-medium text-foreground">Client mappings</h2>
          <p className="text-xs text-muted-foreground">
            Auto-match runs on the bracketed code in the TRMM client name (e.g.{" "}
            <code className="text-foreground">[ACME]</code>). Override per client below;
            manual overrides are kept across re-syncs.
          </p>
        </div>
        <div className="relative">
          <Search className="absolute left-2 top-1/2 h-3 w-3 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter…"
            className="w-48 pl-7"
          />
        </div>
      </div>

      {mappings.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : filtered.length === 0 ? (
        <p className="rounded-md border border-dashed border-glass-strong bg-glass-strong/50 p-4 text-center text-xs text-muted-foreground">
          {mappings.data && mappings.data.items.length > 0
            ? "No mappings match the filter."
            : "No TRMM clients yet — run a sync first."}
        </p>
      ) : (
        <div className="overflow-hidden rounded-md border border-glass">
          <table className="w-full text-sm">
            <thead className="bg-glass-strong/60 text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2 text-left">TRMM client</th>
                <th className="px-3 py-2 text-left">Code</th>
                <th className="px-3 py-2 text-left">Agents</th>
                <th className="px-3 py-2 text-left">Linked company</th>
                <th className="px-3 py-2 text-left">Mapping</th>
                <th className="px-3 py-2 text-right">Action</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((row) => (
                <MappingRow
                  key={row.trmmClientId}
                  row={row}
                  onChanged={() => qc.invalidateQueries({ queryKey: MAPPINGS_QK })}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function MappingRow({
  row,
  onChanged,
}: {
  row: TrmmClientMapping;
  onChanged: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [search, setSearch] = useState("");
  const picker = useQuery({
    queryKey: ["companies", "picker", search],
    queryFn: () => companyApi.picker(search),
    enabled: editing,
  });

  const setMapping = useMutation({
    mutationFn: (companyId: string | null) =>
      trmmAdminApi.setClientMapping(row.trmmClientId, {
        companyId,
        clearOverride: companyId === null,
      }),
    onSuccess: () => {
      toast.success("Mapping updated");
      onChanged();
      setEditing(false);
    },
  });

  return (
    <tr className="border-t border-glass">
      <td className="px-3 py-2 text-foreground">{row.name}</td>
      <td className="px-3 py-2 text-muted-foreground">{row.code ?? "—"}</td>
      <td className="px-3 py-2 text-muted-foreground">{row.agentCount}</td>
      <td className="px-3 py-2">
        {row.companyName ? (
          <span className="text-foreground">
            {row.companyName}{" "}
            {row.companyCode && (
              <span className="text-xs text-muted-foreground">[{row.companyCode}]</span>
            )}
          </span>
        ) : (
          <span className="text-muted-foreground">Unlinked</span>
        )}
      </td>
      <td className="px-3 py-2">
        <Badge
          className={cn(
            "border text-[10px] font-normal",
            row.autoMatched
              ? "border-sky-400/30 bg-sky-500/10 text-sky-300"
              : "border-purple-400/30 bg-purple-500/10 text-purple-300",
          )}
        >
          <Sparkles className="mr-1 h-3 w-3" />
          {row.autoMatched ? "Auto" : "Manual"}
        </Badge>
      </td>
      <td className="px-3 py-2 text-right">
        {editing ? (
          <div className="flex flex-col items-end gap-1">
            <Input
              autoFocus
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search companies…"
              className="w-48"
            />
            <div className="max-h-32 w-48 overflow-y-auto rounded-md border border-glass bg-background/80">
              {(picker.data ?? []).slice(0, 10).map((c) => (
                <button
                  key={c.id}
                  type="button"
                  className="block w-full px-2 py-1 text-left text-xs text-foreground hover:bg-glass"
                  onClick={() => setMapping.mutate(c.id)}
                >
                  {c.name}{" "}
                  <span className="text-muted-foreground">[{c.code}]</span>
                </button>
              ))}
              {picker.data && picker.data.length === 0 && (
                <p className="px-2 py-1 text-xs text-muted-foreground">No matches.</p>
              )}
            </div>
            <div className="flex gap-1">
              <Button size="sm" variant="ghost" onClick={() => setEditing(false)}>
                Cancel
              </Button>
              {!row.autoMatched && (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => setMapping.mutate(null)}
                  disabled={setMapping.isPending}
                >
                  <Link2Off className="mr-1 h-3 w-3" /> Revert to auto
                </Button>
              )}
            </div>
          </div>
        ) : (
          <Button size="sm" variant="ghost" onClick={() => setEditing(true)}>
            <Link2 className="mr-1 h-3 w-3" /> {row.autoMatched ? "Override" : "Change"}
          </Button>
        )}
      </td>
    </tr>
  );
}
