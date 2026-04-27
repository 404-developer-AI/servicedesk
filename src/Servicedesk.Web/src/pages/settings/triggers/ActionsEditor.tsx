import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Plus, Trash2, GripVertical } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  ACTION_KIND_LABELS,
  blankActionForKind,
  type TriggerAction,
} from "./types";
import {
  triggerApi,
  type Queue,
  type Status,
  type Priority,
  type TriggerListItem,
  type TriggerTemplateVariable,
} from "@/lib/api";
import { TriggerTemplateField } from "./TriggerTemplateField";
import { cn } from "@/lib/utils";

type Props = {
  value: TriggerAction[];
  onChange: (next: TriggerAction[]) => void;
  taxonomies: {
    queues: Queue[];
    statuses: Status[];
    priorities: Priority[];
  };
  variables: TriggerTemplateVariable[];
};

const ACTION_KINDS: TriggerAction["kind"][] = [
  "set_queue",
  "set_priority",
  "set_status",
  "set_owner",
  "set_pending_till",
  "add_internal_note",
  "add_public_note",
  "send_mail",
];

/// Renders the per-trigger actions list. Each entry is a typed action
/// block with its own inline form fragment — picking <c>set_priority</c>
/// shows a priority dropdown; <c>send_mail</c> shows recipient + subject +
/// HTML body fields with the variable picker in scope. Order matters: the
/// dispatcher applies actions top-to-bottom so admins can chain "set
/// priority then add a note explaining why".
export function ActionsEditor({ value, onChange, taxonomies, variables }: Props) {
  function update(idx: number, action: TriggerAction) {
    const next = value.slice();
    next[idx] = action;
    onChange(next);
  }
  function remove(idx: number) {
    const next = value.slice();
    next.splice(idx, 1);
    onChange(next);
  }
  function move(idx: number, dir: -1 | 1) {
    const target = idx + dir;
    if (target < 0 || target >= value.length) return;
    const next = value.slice();
    [next[idx], next[target]] = [next[target], next[idx]];
    onChange(next);
  }
  function add(kind: TriggerAction["kind"]) {
    onChange([...value, blankActionForKind(kind)]);
  }

  return (
    <div className="space-y-3">
      {value.length === 0 && (
        <p className="rounded-md border border-dashed border-white/[0.08] bg-white/[0.01] px-3 py-3 text-xs text-muted-foreground/70">
          No actions yet — a trigger with no actions logs every match but
          changes nothing.
        </p>
      )}
      {value.map((action, idx) => (
        <ActionCard
          key={idx}
          action={action}
          idx={idx}
          total={value.length}
          onChange={(a) => update(idx, a)}
          onRemove={() => remove(idx)}
          onMove={(dir) => move(idx, dir)}
          taxonomies={taxonomies}
          variables={variables}
        />
      ))}
      <AddActionMenu onAdd={add} />
    </div>
  );
}

function AddActionMenu({ onAdd }: { onAdd: (kind: TriggerAction["kind"]) => void }) {
  const [open, setOpen] = React.useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className="text-xs">
          <Plus className="h-3.5 w-3.5" />
          Add action
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-56 p-1 border border-white/10 bg-[hsl(240_10%_6%/0.96)] backdrop-blur-xl">
        {ACTION_KINDS.map((k) => (
          <button
            key={k}
            type="button"
            onClick={() => { onAdd(k); setOpen(false); }}
            className="w-full text-left rounded px-2 py-1.5 text-xs text-foreground/80 hover:bg-white/[0.04]"
          >
            {ACTION_KIND_LABELS[k]}
          </button>
        ))}
      </PopoverContent>
    </Popover>
  );
}

function ActionCard({
  action,
  idx,
  total,
  onChange,
  onRemove,
  onMove,
  taxonomies,
  variables,
}: {
  action: TriggerAction;
  idx: number;
  total: number;
  onChange: (a: TriggerAction) => void;
  onRemove: () => void;
  onMove: (dir: -1 | 1) => void;
  taxonomies: Props["taxonomies"];
  variables: TriggerTemplateVariable[];
}) {
  return (
    <div className="rounded-lg border border-white/[0.08] bg-white/[0.02] px-4 py-3">
      <header className="mb-3 flex items-center gap-2">
        <span className="text-muted-foreground/50">
          <GripVertical className="h-4 w-4" />
        </span>
        <span className="rounded-md border border-violet-400/30 bg-violet-400/10 px-2 py-0.5 text-[11px] font-medium text-violet-200">
          {idx + 1}. {ACTION_KIND_LABELS[action.kind]}
        </span>
        <div className="ml-auto flex items-center gap-1">
          <Button
            variant="ghost" size="sm"
            className="h-6 w-6 p-0"
            onClick={() => onMove(-1)}
            disabled={idx === 0}
            title="Move up"
          >▲</Button>
          <Button
            variant="ghost" size="sm"
            className="h-6 w-6 p-0"
            onClick={() => onMove(1)}
            disabled={idx === total - 1}
            title="Move down"
          >▼</Button>
          <Button
            variant="ghost" size="sm"
            className="h-7 text-destructive"
            onClick={onRemove}
            title="Remove action"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      </header>

      <ActionForm
        action={action}
        onChange={onChange}
        taxonomies={taxonomies}
        variables={variables}
      />
    </div>
  );
}

function ActionForm({
  action,
  onChange,
  taxonomies,
  variables,
}: {
  action: TriggerAction;
  onChange: (a: TriggerAction) => void;
  taxonomies: Props["taxonomies"];
  variables: TriggerTemplateVariable[];
}) {
  switch (action.kind) {
    case "set_queue":
      return (
        <FieldRow label="Queue">
          <NativeSelect
            value={action.queue_id}
            onChange={(v) => onChange({ ...action, queue_id: v })}
          >
            <option value="">— Select —</option>
            {taxonomies.queues.map((q) => (
              <option key={q.id} value={q.id}>{q.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
      );
    case "set_priority":
      return (
        <FieldRow label="Priority">
          <NativeSelect
            value={action.priority_id}
            onChange={(v) => onChange({ ...action, priority_id: v })}
          >
            <option value="">— Select —</option>
            {taxonomies.priorities.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
      );
    case "set_status":
      return (
        <FieldRow label="Status">
          <NativeSelect
            value={action.status_id}
            onChange={(v) => onChange({ ...action, status_id: v })}
          >
            <option value="">— Select —</option>
            {taxonomies.statuses.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
      );
    case "set_owner":
      return (
        <FieldRow label="Owner">
          <input
            type="text"
            placeholder="agent user-id (Guid) — leave empty to clear"
            value={action.user_id ?? ""}
            onChange={(e) =>
              onChange({ ...action, user_id: e.target.value.trim() || null })
            }
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          />
        </FieldRow>
      );
    case "set_pending_till": {
      const onModeChange = (next: "absolute" | "relative" | "businessDays" | "clear") => {
        switch (next) {
          case "absolute":
            onChange({ kind: "set_pending_till", mode: "absolute", value: "" });
            break;
          case "relative":
            onChange({ kind: "set_pending_till", mode: "relative", value: "P1D" });
            break;
          case "businessDays":
            onChange({
              kind: "set_pending_till",
              mode: "businessDays",
              business_days: 2,
              wake_at_local: "08:00",
            });
            break;
          case "clear":
            onChange({ kind: "set_pending_till", mode: "clear" });
            break;
        }
      };
      return (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <FieldRow label="Mode">
            <NativeSelect
              value={action.mode}
              onChange={(v) => onModeChange(v as "absolute" | "relative" | "businessDays" | "clear")}
            >
              <option value="relative">Relative (ISO 8601 duration)</option>
              <option value="businessDays">Business days at fixed time</option>
              <option value="absolute">Absolute (ISO 8601)</option>
              <option value="clear">Clear</option>
            </NativeSelect>
          </FieldRow>
          {action.mode === "absolute" && (
            <FieldRow label="Timestamp" className="sm:col-span-2">
              <input
                type="text"
                value={action.value}
                onChange={(e) => onChange({ ...action, value: e.target.value })}
                placeholder="2026-04-30T17:00:00Z"
                className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
              />
            </FieldRow>
          )}
          {action.mode === "relative" && (
            <FieldRow label="Duration" className="sm:col-span-2">
              <input
                type="text"
                value={action.value}
                onChange={(e) => onChange({ ...action, value: e.target.value })}
                placeholder="P1D, PT4H, P3DT12H…"
                className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
              />
            </FieldRow>
          )}
          {action.mode === "businessDays" && (
            <>
              <FieldRow label="Business days">
                <input
                  type="number"
                  min={0}
                  max={365}
                  step={1}
                  value={action.business_days}
                  onChange={(e) =>
                    onChange({
                      ...action,
                      business_days: Math.max(0, Number.parseInt(e.target.value || "0", 10) || 0),
                    })
                  }
                  className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm font-mono text-foreground focus:outline-none focus:ring-1 focus:ring-ring"
                />
              </FieldRow>
              <FieldRow label="Wake-up time (local)">
                <input
                  type="time"
                  value={action.wake_at_local}
                  onChange={(e) => onChange({ ...action, wake_at_local: e.target.value || "08:00" })}
                  className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm font-mono text-foreground focus:outline-none focus:ring-1 focus:ring-ring [color-scheme:dark]"
                />
              </FieldRow>
              <FieldRow label="Schedule" className="sm:col-span-3">
                <p className="rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-[11px] text-muted-foreground/80">
                  Uses the default business-hours schedule from <span className="text-foreground/80">Settings → SLA</span>. Working days, holidays and timezone are taken from there.
                </p>
              </FieldRow>
            </>
          )}
          {action.mode !== "clear" && (
            <FieldRow label="When this expires, run" className="sm:col-span-3">
              <ChainTriggerPicker
                value={action.next_trigger_id ?? ""}
                onChange={(v) =>
                  onChange({ ...action, next_trigger_id: v || null })
                }
              />
            </FieldRow>
          )}
        </div>
      );
    }
    case "add_internal_note":
    case "add_public_note":
      return (
        <FieldRow label="Body">
          <TriggerTemplateField
            mode="html"
            variables={variables}
            value={action.body_html}
            onChange={(v) => onChange({ ...action, body_html: v })}
            placeholder="Note body — supports #{variables}"
          />
        </FieldRow>
      );
    case "send_mail":
      return (
        <div className="space-y-3">
          <FieldRow label="To">
            <NativeSelect
              value={action.to.startsWith("address:") ? "address" : action.to}
              onChange={(v) =>
                onChange({
                  ...action,
                  to: v === "address" ? "address:" : v,
                })
              }
            >
              <option value="customer">Customer</option>
              <option value="owner-agent">Owner agent</option>
              <option value="queue-agents">Queue agents</option>
              <option value="address">Specific address(es)…</option>
            </NativeSelect>
          </FieldRow>
          {action.to.startsWith("address:") && (
            <FieldRow label="Address(es)">
              <input
                type="text"
                value={action.to.slice("address:".length)}
                placeholder="alex@example.com, team@example.com"
                onChange={(e) =>
                  onChange({ ...action, to: `address:${e.target.value}` })
                }
                className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
              />
            </FieldRow>
          )}
          <FieldRow label="Subject">
            <TriggerTemplateField
              mode="plain"
              variables={variables}
              value={action.subject}
              onChange={(v) => onChange({ ...action, subject: v })}
              placeholder="Subject — supports #{variables}"
            />
          </FieldRow>
          <FieldRow label="Body">
            <TriggerTemplateField
              mode="html"
              variables={variables}
              value={action.body_html}
              onChange={(v) => onChange({ ...action, body_html: v })}
              placeholder="Mail body — supports #{variables}"
            />
          </FieldRow>
        </div>
      );
  }
}

function FieldRow({
  label,
  children,
  className,
}: {
  label: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("space-y-1.5", className)}>
      <label className="text-xs font-medium text-muted-foreground">{label}</label>
      {children}
    </div>
  );
}

function NativeSelect({
  value,
  onChange,
  children,
}: {
  value: string;
  onChange: (v: string) => void;
  children: React.ReactNode;
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-ring [&_option]:bg-zinc-900"
    >
      {children}
    </select>
  );
}

/// Picker for "when this pending-till expires, run trigger Y". Restricts
/// the dropdown to time:reminder triggers — those are the only ones that
/// semantically belong as a chain target. Empty value = no chain (reminder
/// scan keeps its default cross-join behaviour for this ticket).
function ChainTriggerPicker({
  value,
  onChange,
}: {
  value: string;
  onChange: (v: string) => void;
}) {
  const triggersQ = useQuery({
    queryKey: ["admin", "triggers"],
    queryFn: () => triggerApi.list(),
    staleTime: 30_000,
  });
  const reminderTriggers = (triggersQ.data?.items ?? []).filter(
    (t: TriggerListItem) => t.activatorKind === "time" && t.activatorMode === "reminder",
  );

  return (
    <div className="space-y-1.5">
      <NativeSelect value={value} onChange={onChange}>
        <option value="">— No chain (default reminder scan) —</option>
        {reminderTriggers.map((t) => (
          <option key={t.id} value={t.id}>
            {t.name}{!t.isActive ? "  (inactive)" : ""}
          </option>
        ))}
      </NativeSelect>
      <p className="text-[11px] text-muted-foreground/70">
        Only time:reminder triggers can be chained. The chain is one-shot — after this trigger fires once, the pointer is cleared and the next pending-cycle re-arms explicitly.
      </p>
    </div>
  );
}
