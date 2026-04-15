import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Save, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { slaApi, taxonomyApi, type SlaPolicy } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";

type CellKey = string; // `${queueId|-}:${priorityId}`

export function PoliciesTab() {
  const qc = useQueryClient();
  const queuesQ = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });
  const prioritiesQ = useQuery({ queryKey: ["taxonomy", "priorities"], queryFn: () => taxonomyApi.priorities.list() });
  const schemasQ = useQuery({ queryKey: ["sla", "schemas"], queryFn: () => slaApi.listSchemas() });
  const policiesQ = useQuery({ queryKey: ["sla", "policies"], queryFn: () => slaApi.listPolicies() });

  const [defaultSchemaId, setDefaultSchemaId] = useState<string | null>(null);

  const byKey = useMemo<Record<CellKey, SlaPolicy>>(() => {
    const map: Record<CellKey, SlaPolicy> = {};
    for (const p of policiesQ.data ?? []) {
      const key = `${p.queueId ?? "-"}:${p.priorityId}`;
      map[key] = p;
    }
    return map;
  }, [policiesQ.data]);

  const upsert = useMutation({
    mutationFn: (body: {
      queueId: string | null;
      priorityId: string;
      businessHoursSchemaId: string;
      firstResponseMinutes: number;
      resolutionMinutes: number;
      pauseOnPending: boolean;
    }) => slaApi.upsertPolicy(body),
    onSuccess: () => {
      toast.success("Policy saved");
      qc.invalidateQueries({ queryKey: ["sla", "policies"] });
    },
    onError: (e: Error) => toast.error(`Save failed: ${e.message}`),
  });

  const remove = useMutation({
    mutationFn: (id: string) => slaApi.deletePolicy(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["sla", "policies"] }),
  });

  if (queuesQ.isLoading || prioritiesQ.isLoading || schemasQ.isLoading || policiesQ.isLoading) {
    return <Skeleton className="h-64 w-full" />;
  }

  const schemas = schemasQ.data ?? [];
  const queues = queuesQ.data ?? [];
  const priorities = prioritiesQ.data ?? [];
  const activeSchemaId = defaultSchemaId ?? schemas.find((s) => s.isDefault)?.id ?? schemas[0]?.id ?? "";

  if (schemas.length === 0) {
    return <p className="text-sm text-muted-foreground">Create a business-hours schema first.</p>;
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-3">
        <label className="text-xs text-muted-foreground">Default business-hours for new policies</label>
        <select
          className="h-9 rounded-md border border-white/[0.06] bg-white/[0.02] px-2 text-sm"
          value={activeSchemaId}
          onChange={(e) => setDefaultSchemaId(e.target.value)}
        >
          {schemas.map((s) => (
            <option key={s.id} value={s.id}>{s.name}</option>
          ))}
        </select>
      </div>

      <div className="overflow-x-auto rounded-md border border-white/[0.06]">
        <table className="w-full text-sm">
          <thead className="bg-white/[0.02] text-xs uppercase tracking-wider text-muted-foreground/60">
            <tr>
              <th className="px-3 py-2 text-left">Queue</th>
              {priorities.map((p) => (
                <th key={p.id} className="px-3 py-2 text-left">{p.name}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {[{ id: null, name: "All queues (fallback)" }, ...queues.map((q) => ({ id: q.id, name: q.name }))].map((q) => (
              <tr key={q.id ?? "-"} className="border-t border-white/[0.04]">
                <td className="px-3 py-2 font-medium">{q.name}</td>
                {priorities.map((p) => {
                  const key = `${q.id ?? "-"}:${p.id}`;
                  const existing = byKey[key];
                  return (
                    <PolicyCell
                      key={key}
                      existing={existing}
                      queueId={q.id}
                      priorityId={p.id}
                      defaultSchemaId={activeSchemaId}
                      onSave={(input) => upsert.mutate(input)}
                      onDelete={(id) => remove.mutate(id)}
                      schemas={schemas}
                    />
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {upsert.isPending && <div className="text-xs text-muted-foreground"><Loader2 className="inline h-3 w-3 animate-spin" /> Saving…</div>}
    </div>
  );
}

function PolicyCell({
  existing,
  queueId,
  priorityId,
  defaultSchemaId,
  schemas,
  onSave,
  onDelete,
}: {
  existing?: SlaPolicy;
  queueId: string | null;
  priorityId: string;
  defaultSchemaId: string;
  schemas: { id: string; name: string }[];
  onSave: (input: {
    queueId: string | null;
    priorityId: string;
    businessHoursSchemaId: string;
    firstResponseMinutes: number;
    resolutionMinutes: number;
    pauseOnPending: boolean;
  }) => void;
  onDelete: (id: string) => void;
}) {
  const [fr, setFr] = useState(existing?.firstResponseMinutes ?? 60);
  const [res, setRes] = useState(existing?.resolutionMinutes ?? 240);
  const [schemaId, setSchemaId] = useState(existing?.businessHoursSchemaId ?? defaultSchemaId);
  const [pause, setPause] = useState(existing?.pauseOnPending ?? true);

  return (
    <td className="px-3 py-2 align-top">
      <div className="flex flex-col gap-1">
        <div className="flex items-center gap-1 text-xs">
          <span className="text-muted-foreground/60">FR</span>
          <Input type="number" value={fr} onChange={(e) => setFr(parseInt(e.target.value || "0", 10))} className="h-7 w-20" />
          <span className="text-muted-foreground/60">m</span>
        </div>
        <div className="flex items-center gap-1 text-xs">
          <span className="text-muted-foreground/60">Res</span>
          <Input type="number" value={res} onChange={(e) => setRes(parseInt(e.target.value || "0", 10))} className="h-7 w-20" />
          <span className="text-muted-foreground/60">m</span>
        </div>
        <select
          value={schemaId}
          onChange={(e) => setSchemaId(e.target.value)}
          className="h-7 rounded-md border border-white/[0.06] bg-white/[0.02] px-1 text-xs"
        >
          {schemas.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
        </select>
        <label className="flex items-center gap-1 text-xs text-muted-foreground">
          <input type="checkbox" checked={pause} onChange={(e) => setPause(e.target.checked)} />
          Pause on Pending
        </label>
        <div className="flex gap-1">
          <Button
            variant="ghost"
            size="sm"
            className="h-7 px-2"
            onClick={() => onSave({ queueId, priorityId, businessHoursSchemaId: schemaId, firstResponseMinutes: fr, resolutionMinutes: res, pauseOnPending: pause })}
          >
            <Save className="h-3 w-3" />
          </Button>
          {existing && (
            <Button variant="ghost" size="sm" className="h-7 px-2" onClick={() => onDelete(existing.id)}>
              <Trash2 className="h-3 w-3" />
            </Button>
          )}
        </div>
      </div>
    </td>
  );
}
