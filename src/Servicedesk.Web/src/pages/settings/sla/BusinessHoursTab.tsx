import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Plus, Save, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { slaApi, type BusinessHoursSchema } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";

const SCHEMAS_KEY = ["sla", "schemas"] as const;
const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

function mmToHHMM(m: number) {
  const h = Math.floor(m / 60)
    .toString()
    .padStart(2, "0");
  const mm = (m % 60).toString().padStart(2, "0");
  return `${h}:${mm}`;
}

function hhmmToM(v: string) {
  const [h, m] = v.split(":").map((x) => parseInt(x, 10));
  return (h || 0) * 60 + (m || 0);
}

type Draft = {
  id: string | null;
  name: string;
  timezone: string;
  countryCode: string;
  isDefault: boolean;
  slots: { dayOfWeek: number; start: string; end: string }[];
};

function scheduleToSlots(schedule: Draft["slots"]) {
  return schedule.map((s) => ({
    dayOfWeek: s.dayOfWeek,
    startMinute: hhmmToM(s.start),
    endMinute: hhmmToM(s.end),
  }));
}

function schemaToDraft(schema: BusinessHoursSchema | null): Draft {
  if (!schema) {
    const slots = [1, 2, 3, 4, 5].map((d) => ({ dayOfWeek: d, start: "09:00", end: "17:00" }));
    return {
      id: null,
      name: "New schema",
      timezone: "Europe/Brussels",
      countryCode: "BE",
      isDefault: false,
      slots,
    };
  }
  return {
    id: schema.id,
    name: schema.name,
    timezone: schema.timezone,
    countryCode: schema.countryCode,
    isDefault: schema.isDefault,
    slots: schema.slots.map((s) => ({
      dayOfWeek: s.dayOfWeek,
      start: mmToHHMM(s.startMinute),
      end: mmToHHMM(s.endMinute),
    })),
  };
}

export function BusinessHoursTab() {
  const qc = useQueryClient();
  const schemasQuery = useQuery({ queryKey: SCHEMAS_KEY, queryFn: () => slaApi.listSchemas() });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);

  const selected = useMemo<BusinessHoursSchema | null>(() => {
    if (!schemasQuery.data) return null;
    if (selectedId === null) return schemasQuery.data[0] ?? null;
    return schemasQuery.data.find((s) => s.id === selectedId) ?? null;
  }, [schemasQuery.data, selectedId]);

  useEffect(() => {
    if (selected && draft?.id !== selected.id) {
      setDraft(schemaToDraft(selected));
    }
  }, [selected, draft?.id]);

  const save = useMutation({
    mutationFn: async (d: Draft) => {
      const body = {
        name: d.name,
        timezone: d.timezone,
        countryCode: d.countryCode,
        isDefault: d.isDefault,
        slots: scheduleToSlots(d.slots),
      };
      return d.id ? slaApi.updateSchema(d.id, body) : slaApi.createSchema(body);
    },
    onSuccess: () => {
      toast.success("Business hours saved");
      qc.invalidateQueries({ queryKey: SCHEMAS_KEY });
    },
    onError: (e: Error) => toast.error(`Save failed: ${e.message}`),
  });

  const remove = useMutation({
    mutationFn: (id: string) => slaApi.deleteSchema(id),
    onSuccess: () => {
      toast.success("Schema deleted");
      setSelectedId(null);
      setDraft(null);
      qc.invalidateQueries({ queryKey: SCHEMAS_KEY });
    },
    onError: (e: Error) => toast.error(`Delete failed: ${e.message}`),
  });

  if (schemasQuery.isLoading) return <Skeleton className="h-64 w-full" />;

  return (
    <div className="grid gap-4 md:grid-cols-[220px_1fr]">
      <aside className="flex flex-col gap-2">
        {schemasQuery.data?.map((s) => (
          <button
            key={s.id}
            type="button"
            onClick={() => setSelectedId(s.id)}
            className={`rounded-md border px-3 py-2 text-left text-sm transition ${
              selected?.id === s.id
                ? "border-primary/40 bg-primary/10 text-foreground"
                : "border-white/[0.06] bg-white/[0.02] text-muted-foreground hover:bg-white/[0.05]"
            }`}
          >
            <div className="font-medium text-foreground">{s.name}</div>
            <div className="text-xs text-muted-foreground">
              {s.timezone}
              {s.isDefault ? " · default" : ""}
            </div>
          </button>
        ))}
        <Button
          variant="ghost"
          size="sm"
          onClick={() => {
            setSelectedId(null);
            setDraft(schemaToDraft(null));
          }}
          className="justify-start"
        >
          <Plus className="mr-2 h-4 w-4" /> New schema
        </Button>
      </aside>

      {draft && (
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
            <label className="space-y-1 text-xs text-muted-foreground">
              Name
              <Input
                value={draft.name}
                onChange={(e) => setDraft({ ...draft, name: e.target.value })}
              />
            </label>
            <label className="space-y-1 text-xs text-muted-foreground">
              Timezone
              <Input
                value={draft.timezone}
                onChange={(e) => setDraft({ ...draft, timezone: e.target.value })}
                placeholder="Europe/Brussels"
              />
            </label>
            <label className="space-y-1 text-xs text-muted-foreground">
              Country code (ISO-2)
              <Input
                value={draft.countryCode}
                onChange={(e) => setDraft({ ...draft, countryCode: e.target.value.toUpperCase() })}
                maxLength={2}
              />
            </label>
          </div>

          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={draft.isDefault}
              onChange={(e) => setDraft({ ...draft, isDefault: e.target.checked })}
            />
            Use as default schema
          </label>

          <div className="space-y-2">
            <div className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
              Weekly slots
            </div>
            {DAYS.map((dayName, dayIdx) => {
              const daySlots = draft.slots.map((s, i) => ({ ...s, i })).filter((s) => s.dayOfWeek === dayIdx);
              return (
                <div key={dayIdx} className="flex flex-wrap items-center gap-2 border-b border-white/[0.04] py-2">
                  <div className="w-12 text-sm text-muted-foreground">{dayName}</div>
                  {daySlots.length === 0 && (
                    <div className="text-xs text-muted-foreground/60">Closed</div>
                  )}
                  {daySlots.map((s) => (
                    <div key={s.i} className="flex items-center gap-1">
                      <Input
                        type="time"
                        value={s.start}
                        onChange={(e) => {
                          const next = [...draft.slots];
                          next[s.i] = { ...next[s.i], start: e.target.value };
                          setDraft({ ...draft, slots: next });
                        }}
                        className="w-24"
                      />
                      <span className="text-muted-foreground">–</span>
                      <Input
                        type="time"
                        value={s.end}
                        onChange={(e) => {
                          const next = [...draft.slots];
                          next[s.i] = { ...next[s.i], end: e.target.value };
                          setDraft({ ...draft, slots: next });
                        }}
                        className="w-24"
                      />
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() =>
                          setDraft({
                            ...draft,
                            slots: draft.slots.filter((_, i) => i !== s.i),
                          })
                        }
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  ))}
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() =>
                      setDraft({
                        ...draft,
                        slots: [...draft.slots, { dayOfWeek: dayIdx, start: "09:00", end: "17:00" }],
                      })
                    }
                  >
                    <Plus className="mr-1 h-3 w-3" /> Add slot
                  </Button>
                </div>
              );
            })}
          </div>

          <div className="flex items-center gap-2">
            <Button onClick={() => save.mutate(draft)} disabled={save.isPending}>
              {save.isPending ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Save className="mr-2 h-4 w-4" />}
              Save
            </Button>
            {draft.id && (
              <Button
                variant="ghost"
                onClick={() => {
                  if (window.confirm("Delete this schema? This cannot be undone.")) remove.mutate(draft.id!);
                }}
                disabled={remove.isPending}
              >
                <Trash2 className="mr-2 h-4 w-4" /> Delete
              </Button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
