import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Plus, RefreshCw, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { slaApi } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";

export function HolidaysTab() {
  const qc = useQueryClient();
  const schemas = useQuery({ queryKey: ["sla", "schemas"], queryFn: () => slaApi.listSchemas() });
  const countries = useQuery({ queryKey: ["sla", "countries"], queryFn: () => slaApi.listCountries() });

  const [schemaId, setSchemaId] = useState<string | null>(null);
  const [year, setYear] = useState<number>(new Date().getFullYear());
  const [newDate, setNewDate] = useState("");
  const [newName, setNewName] = useState("");

  const active = useMemo(() => {
    if (!schemas.data) return null;
    return schemas.data.find((s) => s.id === schemaId) ?? schemas.data[0] ?? null;
  }, [schemas.data, schemaId]);

  const holidays = useQuery({
    queryKey: ["sla", "holidays", active?.id, year],
    queryFn: () => slaApi.listHolidays(active!.id, year),
    enabled: !!active,
  });

  const sync = useMutation({
    mutationFn: (countryCode: string) => slaApi.syncHolidays(active!.id, countryCode, year),
    onSuccess: () => {
      toast.success("Holidays synced");
      qc.invalidateQueries({ queryKey: ["sla", "holidays"] });
    },
    onError: (e: Error) => toast.error(`Sync failed: ${e.message}`),
  });

  const addManual = useMutation({
    mutationFn: () =>
      slaApi.addHoliday(active!.id, { date: newDate, name: newName, countryCode: active!.countryCode }),
    onSuccess: () => {
      toast.success("Holiday added");
      setNewDate("");
      setNewName("");
      qc.invalidateQueries({ queryKey: ["sla", "holidays"] });
    },
    onError: (e: Error) => toast.error(`Add failed: ${e.message}`),
  });

  const remove = useMutation({
    mutationFn: (id: number) => slaApi.deleteHoliday(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["sla", "holidays"] }),
  });

  if (schemas.isLoading) return <Skeleton className="h-48 w-full" />;
  if (!active) return <p className="text-sm text-muted-foreground">Create a business-hours schema first.</p>;

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-1 gap-3 md:grid-cols-4">
        <label className="space-y-1 text-xs text-muted-foreground">
          Schema
          <select
            className="h-9 w-full rounded-md border border-white/[0.06] bg-white/[0.02] px-2 text-sm"
            value={active.id}
            onChange={(e) => setSchemaId(e.target.value)}
          >
            {schemas.data!.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </select>
        </label>
        <label className="space-y-1 text-xs text-muted-foreground">
          Year
          <Input type="number" value={year} onChange={(e) => setYear(parseInt(e.target.value, 10))} />
        </label>
        <label className="space-y-1 text-xs text-muted-foreground">
          Country (auto-sync)
          <select
            className="h-9 w-full rounded-md border border-white/[0.06] bg-white/[0.02] px-2 text-sm"
            value={active.countryCode}
            onChange={(e) => sync.mutate(e.target.value)}
          >
            <option value="">— select —</option>
            {(countries.data ?? []).map((c) => (
              <option key={c.countryCode} value={c.countryCode}>{c.name} ({c.countryCode})</option>
            ))}
          </select>
        </label>
        <div className="flex items-end">
          <Button
            variant="ghost"
            onClick={() => active.countryCode && sync.mutate(active.countryCode)}
            disabled={sync.isPending || !active.countryCode}
          >
            {sync.isPending ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RefreshCw className="mr-2 h-4 w-4" />}
            Sync {active.countryCode || ""} {year}
          </Button>
        </div>
      </div>

      <div className="flex flex-wrap items-end gap-2 rounded-md border border-white/[0.06] bg-white/[0.02] p-3">
        <label className="space-y-1 text-xs text-muted-foreground">
          Add date
          <Input type="date" value={newDate} onChange={(e) => setNewDate(e.target.value)} />
        </label>
        <label className="flex-1 space-y-1 text-xs text-muted-foreground">
          Name
          <Input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="Bedrijfsvakantie" />
        </label>
        <Button
          onClick={() => addManual.mutate()}
          disabled={!newDate || addManual.isPending}
        >
          <Plus className="mr-2 h-4 w-4" /> Add
        </Button>
      </div>

      <div className="overflow-hidden rounded-md border border-white/[0.06]">
        <table className="w-full text-sm">
          <thead className="bg-white/[0.02] text-xs uppercase tracking-wider text-muted-foreground/60">
            <tr>
              <th className="px-3 py-2 text-left">Date</th>
              <th className="px-3 py-2 text-left">Name</th>
              <th className="px-3 py-2 text-left">Source</th>
              <th className="px-3 py-2" />
            </tr>
          </thead>
          <tbody>
            {(holidays.data ?? []).map((h) => (
              <tr key={h.id} className="border-t border-white/[0.04]">
                <td className="px-3 py-2">{h.date.substring(0, 10)}</td>
                <td className="px-3 py-2">{h.name}</td>
                <td className="px-3 py-2 text-xs text-muted-foreground">{h.source}</td>
                <td className="px-3 py-2 text-right">
                  <Button variant="ghost" size="icon" onClick={() => remove.mutate(h.id)}>
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </td>
              </tr>
            ))}
            {(holidays.data?.length ?? 0) === 0 && (
              <tr>
                <td colSpan={4} className="px-3 py-8 text-center text-xs text-muted-foreground">
                  No holidays for {year}. Use the country dropdown to auto-sync.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
