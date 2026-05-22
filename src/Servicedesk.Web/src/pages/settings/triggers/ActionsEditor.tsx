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
  KNOWN_ACTION_KINDS,
  blankActionForKind,
  type KnownActionKind,
  type PromptQuestion,
  type TriggerAction,
} from "./types";
import {
  triggerApi,
  type Category,
  type Queue,
  type Status,
  type Priority,
  type TriggerListItem,
  type TriggerTemplateVariable,
} from "@/lib/api";
import { surveysAgentApi } from "@/lib/surveys-api";
import { TriggerTemplateField } from "./TriggerTemplateField";
import { AgentPicker } from "@/components/AgentPicker";
import { CompanyPicker } from "@/components/CompanyPicker";
import { ContactPicker } from "@/components/ContactPicker";
import { cn } from "@/lib/utils";

type Props = {
  value: TriggerAction[];
  onChange: (next: TriggerAction[]) => void;
  taxonomies: {
    queues: Queue[];
    statuses: Status[];
    priorities: Priority[];
    categories?: Category[];
  };
  variables: TriggerTemplateVariable[];
  /// v0.0.42 — drives which action kinds the "Add action" menu offers.
  /// Gate triggers expose prompt_confirm only, action/time/manual hide
  /// it. Omitted = all kinds (legacy default for callers that haven't
  /// been threaded through yet).
  activatorKind?: string;
};

const ACTION_KINDS: KnownActionKind[] = [...KNOWN_ACTION_KINDS];

/// Filter the action picker by activator kind. Gates may only carry
/// prompt_confirm; every other activator hides prompt_confirm so the
/// admin cannot save a payload the BE validator will reject.
function allowedKindsFor(activatorKind?: string): KnownActionKind[] {
  if (activatorKind === "gate") return ["prompt_confirm"];
  return ACTION_KINDS.filter((k) => k !== "prompt_confirm");
}

/// Renders the per-trigger actions list. Each entry is a typed action
/// block with its own inline form fragment — picking <c>set_priority</c>
/// shows a priority dropdown; <c>send_mail</c> shows recipient + subject +
/// HTML body fields with the variable picker in scope. Order matters: the
/// dispatcher applies actions top-to-bottom so admins can chain "set
/// priority then add a note explaining why".
export function ActionsEditor({ value, onChange, taxonomies, variables, activatorKind }: Props) {
  const addableKinds = allowedKindsFor(activatorKind);
  const hideAdd = activatorKind === "gate"
    && value.some((a) => a.kind === "prompt_confirm");
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
  function add(kind: KnownActionKind) {
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
          peerActions={value}
          onChange={(a) => update(idx, a)}
          onRemove={() => remove(idx)}
          onMove={(dir) => move(idx, dir)}
          taxonomies={taxonomies}
          variables={variables}
        />
      ))}
      {!hideAdd && <AddActionMenu kinds={addableKinds} onAdd={add} />}
    </div>
  );
}

function AddActionMenu({
  kinds,
  onAdd,
}: {
  kinds: KnownActionKind[];
  onAdd: (kind: KnownActionKind) => void;
}) {
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
        {kinds.map((k) => (
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
  peerActions,
  onChange,
  onRemove,
  onMove,
  taxonomies,
  variables,
}: {
  action: TriggerAction;
  idx: number;
  total: number;
  peerActions: TriggerAction[];
  onChange: (a: TriggerAction) => void;
  onRemove: () => void;
  onMove: (dir: -1 | 1) => void;
  taxonomies: Props["taxonomies"];
  variables: TriggerTemplateVariable[];
}) {
  const isUnknown = action.kind === "__unknown";
  const label = isUnknown
    ? `Unknown action: ${(action as { original_kind: string }).original_kind}`
    : ACTION_KIND_LABELS[action.kind as KnownActionKind];
  return (
    <div className={cn(
      "rounded-lg border px-4 py-3",
      isUnknown
        ? "border-amber-400/30 bg-amber-400/[0.04]"
        : "border-white/[0.08] bg-white/[0.02]",
    )}>
      <header className="mb-3 flex items-center gap-2">
        <span className="text-muted-foreground/50">
          <GripVertical className="h-4 w-4" />
        </span>
        <span className={cn(
          "rounded-md border px-2 py-0.5 text-[11px] font-medium",
          isUnknown
            ? "border-amber-400/40 bg-amber-400/10 text-amber-200"
            : "border-violet-400/30 bg-violet-400/10 text-violet-200",
        )}>
          {idx + 1}. {label}
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
        peerActions={peerActions}
        onChange={onChange}
        taxonomies={taxonomies}
        variables={variables}
      />
    </div>
  );
}

function ActionForm({
  action,
  peerActions,
  onChange,
  taxonomies,
  variables,
}: {
  action: TriggerAction;
  peerActions: TriggerAction[];
  onChange: (a: TriggerAction) => void;
  taxonomies: Props["taxonomies"];
  variables: TriggerTemplateVariable[];
}) {
  switch (action.kind) {
    case "__unknown":
      return (
        <div className="space-y-2">
          <p className="text-xs text-amber-200/80">
            This action's kind is not recognised by this editor build. Save will be rejected by the server until you remove or replace the entry. The original payload is preserved below.
          </p>
          <pre className="rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-[11px] font-mono text-muted-foreground/80 overflow-x-auto">
            {JSON.stringify(action.raw, null, 2)}
          </pre>
        </div>
      );
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
    case "set_status": {
      // v0.0.40 polish — when this trigger also flips the queue,
      // filter the status dropdown to that target queue's allowed-list.
      // Surfaces the same constraint the runtime enforces: you can't
      // save a `set_status` that the target queue would reject.
      const peerSetQueue = peerActions.find((a): a is Extract<TriggerAction, { kind: "set_queue" }> => a.kind === "set_queue");
      const targetQueue = peerSetQueue
        ? taxonomies.queues.find((q) => q.id === peerSetQueue.queue_id)
        : null;
      const targetAllowed = targetQueue?.allowedStatusIds ?? [];
      const scopedStatuses = targetAllowed.length === 0
        ? taxonomies.statuses
        : taxonomies.statuses.filter((s) => targetAllowed.includes(s.id));
      return (
        <FieldRow label="Status">
          <NativeSelect
            value={action.status_id}
            onChange={(v) => onChange({ ...action, status_id: v })}
          >
            <option value="">— Select —</option>
            {scopedStatuses.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </NativeSelect>
          {targetQueue && targetAllowed.length > 0 && (
            <p className="mt-1 text-[11px] text-muted-foreground/70">
              Filtered to the {targetQueue.name} queue's allowed statuses (set_queue is also active in this trigger).
            </p>
          )}
        </FieldRow>
      );
    }
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
    case "repost_as_public_reply":
      return (
        <p className="rounded-md border border-white/[0.06] bg-white/[0.02] px-3 py-2 text-[11px] text-muted-foreground/80">
          Duplicates the triggering article verbatim as a public reply
          (same body, formatting, and attachments-link). Pair with an{" "}
          <span className="text-foreground/80">Article is internal = true</span>{" "}
          condition to prevent the public copy from re-firing the trigger.
        </p>
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
    case "send_survey":
      return <SendSurveyFields action={action} onChange={onChange} />;
    case "create_linked_ticket":
      return (
        <CreateLinkedTicketFields
          action={action}
          onChange={onChange}
          taxonomies={taxonomies}
          variables={variables}
        />
      );
    case "prompt_confirm":
      return (
        <PromptConfirmFields
          action={action}
          onChange={onChange}
          taxonomies={taxonomies}
          variables={variables}
        />
      );
  }
}

function SendSurveyFields({
  action,
  onChange,
}: {
  action: Extract<TriggerAction, { kind: "send_survey" }>;
  onChange: (next: TriggerAction) => void;
}) {
  const surveysQ = useQuery({
    queryKey: ["surveys", "usable"],
    queryFn: () => surveysAgentApi.usable(),
    staleTime: 60_000,
  });
  const ttlText = action.ttl_days_override?.toString() ?? "";
  return (
    <div className="space-y-3">
      <FieldRow label="Survey">
        <NativeSelect
          value={action.survey_id}
          onChange={(v) => onChange({ ...action, survey_id: v })}
        >
          <option value="">— pick a survey —</option>
          {(surveysQ.data ?? []).map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </NativeSelect>
      </FieldRow>
      <FieldRow label="TTL override (days, optional)">
        <input
          type="number"
          value={ttlText}
          min={1}
          max={365}
          onChange={(e) => {
            const raw = e.target.value;
            const next = raw === "" ? null : Math.max(1, Math.min(365, Number(raw)));
            onChange({ ...action, ttl_days_override: next });
          }}
          placeholder="Survey-level default"
          className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
        />
      </FieldRow>
      <FieldRow label="Recipient override (optional)">
        <input
          type="email"
          value={action.recipient_override ?? ""}
          onChange={(e) =>
            onChange({
              ...action,
              recipient_override: e.target.value || null,
            })
          }
          placeholder="Defaults to the ticket requester's email"
          className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
        />
      </FieldRow>
      <p className="text-[11px] text-muted-foreground/80">
        Idempotent: dispatch is skipped while an active invitation already
        exists for this (survey, ticket) pair.
      </p>
    </div>
  );
}

function CreateLinkedTicketFields({
  action,
  onChange,
  taxonomies,
  variables,
}: {
  action: Extract<TriggerAction, { kind: "create_linked_ticket" }>;
  onChange: (next: TriggerAction) => void;
  taxonomies: Props["taxonomies"];
  variables: TriggerTemplateVariable[];
}) {
  const showFixedContact = action.requester_source === "fixed_contact";
  const showFixedCompany = action.company_source === "fixed_company";
  const hasInitialNote = action.initial_note !== null;
  return (
    <div className="space-y-3">
      <FieldRow label="Subject template">
        <input
          type="text"
          value={action.subject_template}
          onChange={(e) => onChange({ ...action, subject_template: e.target.value })}
          placeholder='e.g. "#{ticket.company.name} — replacement order"'
          className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
        />
      </FieldRow>
      <FieldRow label="Description (body) template">
        <TriggerTemplateField
          value={action.body_html_template}
          onChange={(html) => onChange({ ...action, body_html_template: html })}
          variables={variables}
          mode="html"
          placeholder="Optional. Rendered as the new ticket's description."
        />
      </FieldRow>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <FieldRow label="Queue (default)">
          <NativeSelect
            value={action.queue_id ?? ""}
            onChange={(v) => onChange({ ...action, queue_id: v || null })}
          >
            <option value="">— inherit from parent —</option>
            {taxonomies.queues.map((q) => (
              <option key={q.id} value={q.id}>{q.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
        <FieldRow label="Status (default)">
          <NativeSelect
            value={action.status_id ?? ""}
            onChange={(v) => onChange({ ...action, status_id: v || null })}
          >
            <option value="">— inherit from parent —</option>
            {(() => {
              // v0.0.40 polish — when this action also picks a queue,
              // scope the status options to that queue's allowed-list.
              const linkedQueue = action.queue_id
                ? taxonomies.queues.find((q) => q.id === action.queue_id)
                : null;
              const allowed = linkedQueue?.allowedStatusIds ?? [];
              const list = allowed.length === 0
                ? taxonomies.statuses
                : taxonomies.statuses.filter((s) => allowed.includes(s.id));
              return list.map((s) => (
                <option key={s.id} value={s.id}>{s.name}</option>
              ));
            })()}
          </NativeSelect>
        </FieldRow>
        <FieldRow label="Priority (default)">
          <NativeSelect
            value={action.priority_id ?? ""}
            onChange={(v) => onChange({ ...action, priority_id: v || null })}
          >
            <option value="">— inherit from parent —</option>
            {taxonomies.priorities.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
        <FieldRow label="Category (optional)">
          <NativeSelect
            value={action.category_id ?? ""}
            onChange={(v) => onChange({ ...action, category_id: v || null })}
          >
            <option value="">— none —</option>
            {(taxonomies.categories ?? []).map((c) => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
      </div>

      <FieldRow label="Assignee (optional)">
        <AgentPicker
          value={action.assignee_user_id}
          onChange={(userId) => onChange({ ...action, assignee_user_id: userId })}
          placeholder="Unassigned"
        />
      </FieldRow>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <FieldRow label="Requester source">
          <NativeSelect
            value={action.requester_source}
            onChange={(v) => onChange({ ...action, requester_source: v as typeof action.requester_source })}
          >
            <option value="parent">Parent ticket's requester</option>
            <option value="fixed_contact">Fixed contact</option>
            <option value="current_agent">Current agent</option>
          </NativeSelect>
        </FieldRow>
        {showFixedContact && (
          <FieldRow label="Fixed contact">
            <ContactPicker
              value={action.requester_contact_id}
              onChange={(contactId) => onChange({ ...action, requester_contact_id: contactId })}
              placeholder="Search contact…"
            />
          </FieldRow>
        )}
      </div>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <FieldRow label="Company source">
          <NativeSelect
            value={action.company_source}
            onChange={(v) => onChange({ ...action, company_source: v as typeof action.company_source })}
          >
            <option value="parent">Parent ticket's company</option>
            <option value="from_requester_primary">Requester's primary company</option>
            <option value="fixed_company">Fixed company</option>
          </NativeSelect>
        </FieldRow>
        {showFixedCompany && (
          <FieldRow label="Fixed company">
            <CompanyPicker
              value={action.company_id}
              onChange={(companyId) => onChange({ ...action, company_id: companyId })}
              placeholder="Search company…"
            />
          </FieldRow>
        )}
      </div>

      <div className="rounded-md border border-white/[0.06] bg-white/[0.02] p-3">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={hasInitialNote}
            onChange={(e) =>
              onChange({
                ...action,
                initial_note: e.target.checked
                  ? { body_html_template: "", is_internal: true }
                  : null,
              })
            }
          />
          Post an initial note alongside the ticket
        </label>
        {hasInitialNote && action.initial_note && (
          <div className="mt-3 space-y-3">
            <FieldRow label="Note body template">
              <TriggerTemplateField
                value={action.initial_note.body_html_template}
                onChange={(html) =>
                  onChange({
                    ...action,
                    initial_note: action.initial_note
                      ? { ...action.initial_note, body_html_template: html }
                      : null,
                  })
                }
                variables={variables}
                mode="html"
                placeholder="Rendered as the first timeline event after the ticket is created."
              />
            </FieldRow>
            <FieldRow label="Visibility">
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() =>
                    onChange({
                      ...action,
                      initial_note: action.initial_note
                        ? { ...action.initial_note, is_internal: true }
                        : null,
                    })
                  }
                  className={cn(
                    "rounded-md border px-3 py-1 text-xs",
                    action.initial_note.is_internal
                      ? "border-amber-400/40 bg-amber-400/10 text-amber-200"
                      : "border-white/10 bg-white/[0.04] text-muted-foreground hover:text-foreground",
                  )}
                >
                  Internal note
                </button>
                <button
                  type="button"
                  onClick={() =>
                    onChange({
                      ...action,
                      initial_note: action.initial_note
                        ? { ...action.initial_note, is_internal: false }
                        : null,
                    })
                  }
                  className={cn(
                    "rounded-md border px-3 py-1 text-xs",
                    !action.initial_note.is_internal
                      ? "border-emerald-400/40 bg-emerald-400/10 text-emerald-200"
                      : "border-white/10 bg-white/[0.04] text-muted-foreground hover:text-foreground",
                  )}
                >
                  Public reply
                </button>
              </div>
            </FieldRow>
          </div>
        )}
      </div>
    </div>
  );
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

/// Form fragment for the prompt_confirm action — the single action
/// allowed on a gate:status_change trigger. Renders the source/target
/// status pickers, an optional message body, the questions list editor,
/// the bottom-button labels and the note template + visibility
/// selector. The to_status_id is required; from_status_id is optional
/// ("from any" when empty).
function PromptConfirmFields({
  action,
  onChange,
  taxonomies,
  variables,
}: {
  action: Extract<TriggerAction, { kind: "prompt_confirm" }>;
  onChange: (next: TriggerAction) => void;
  taxonomies: Props["taxonomies"];
  variables: TriggerTemplateVariable[];
}) {
  const statuses = taxonomies.statuses;
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <FieldRow label="From status (optional)">
          <NativeSelect
            value={action.from_status_id ?? ""}
            onChange={(v) => onChange({ ...action, from_status_id: v || null })}
          >
            <option value="">— Any source status —</option>
            {statuses.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
        <FieldRow label="To status (required)">
          <NativeSelect
            value={action.to_status_id}
            onChange={(v) => onChange({ ...action, to_status_id: v })}
          >
            <option value="">— Select target status —</option>
            {statuses.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
      </div>
      <FieldRow label="Dialog title">
        <input
          type="text"
          value={action.title}
          onChange={(e) => onChange({ ...action, title: e.target.value })}
          placeholder="Confirm ticket close"
          className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
        />
      </FieldRow>

      <FieldRow label="Message">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={action.show_message}
            onChange={(e) => onChange({ ...action, show_message: e.target.checked })}
            className="h-4 w-4 rounded border-white/20 bg-white/[0.04]"
          />
          Show a message above the questions
        </label>
        {action.show_message && (
          <textarea
            value={action.message}
            onChange={(e) => onChange({ ...action, message: e.target.value })}
            rows={3}
            placeholder="Did you complete every required step before closing?"
            className="mt-2 w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          />
        )}
      </FieldRow>

      <FieldRow label="Questions">
        <PromptQuestionsEditor
          value={action.questions}
          onChange={(qs) => onChange({ ...action, questions: qs })}
        />
        <p className="mt-1 text-[11px] text-muted-foreground/70">
          Mix free-text fields and yes/no button rows. Each question's
          key becomes the <code className="font-mono">#{"{prompt.<key>}"}</code> token in the note template.
        </p>
      </FieldRow>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <FieldRow label="Confirm button label">
          <input
            type="text"
            value={action.confirm_label}
            onChange={(e) => onChange({ ...action, confirm_label: e.target.value })}
            placeholder="Yes, completed"
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          />
        </FieldRow>
        <FieldRow label="Cancel button label">
          <input
            type="text"
            value={action.cancel_label}
            onChange={(e) => onChange({ ...action, cancel_label: e.target.value })}
            placeholder="Cancel"
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          />
        </FieldRow>
      </div>
      <FieldRow label="Confirmation note visibility">
        <NativeSelect
          value={action.note_visibility}
          onChange={(v) => onChange({ ...action, note_visibility: v === "public" ? "public" : "internal" })}
        >
          <option value="internal">Internal note</option>
          <option value="public">Public note</option>
        </NativeSelect>
      </FieldRow>
      <FieldRow label="Note template (optional)">
        <TriggerTemplateField
          mode="html"
          variables={variables}
          value={action.note_template}
          onChange={(v) => onChange({ ...action, note_template: v })}
          placeholder="Confirmed by #{agent.name} — #{prompt.<key>}"
        />
        <p className="mt-1 text-[11px] text-muted-foreground/70">
          Use <code className="font-mono">#{"{prompt.<key>}"}</code> to inject each question's answer (the clicked Yes/No label, or the typed text) and <code className="font-mono">#{"{agent.name}"}</code> for the confirming agent. Leave the template empty to skip the note entirely.
        </p>
      </FieldRow>
    </div>
  );
}

/// Generates a stable initial key for a freshly-added question. Falls
/// back to `q1`, `q2`, … so the admin can rename without thinking about
/// uniqueness up-front. The validator enforces the alphanumeric rule;
/// duplicates are also caught at save-time.
function nextQuestionKey(existing: PromptQuestion[]): string {
  for (let i = 1; i < 1000; i++) {
    const candidate = `q${i}`;
    if (!existing.some((q) => q.key === candidate)) return candidate;
  }
  return `q${Math.floor(Math.random() * 100000)}`;
}

/// In-place list editor for the prompt_confirm questions array. Each
/// row gets a type badge, key input, label input and the type-specific
/// fields (required-flag for text, two button-label slots with hide
/// toggles for yesno). Move-up/move-down + remove buttons sit on the
/// right-hand side; the add menu offers Text and Yes/No.
function PromptQuestionsEditor({
  value,
  onChange,
}: {
  value: PromptQuestion[];
  onChange: (next: PromptQuestion[]) => void;
}) {
  function update(idx: number, q: PromptQuestion) {
    const next = value.slice();
    next[idx] = q;
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
  function add(type: PromptQuestion["type"]) {
    const key = nextQuestionKey(value);
    const blank: PromptQuestion = type === "text"
      ? { key, type: "text", label: "", required: false }
      : { key, type: "yesno", label: "", yes_label: "Yes", no_label: "No" };
    onChange([...value, blank]);
  }
  return (
    <div className="space-y-2">
      {value.length === 0 && (
        <p className="rounded-md border border-dashed border-white/[0.08] bg-white/[0.01] px-3 py-3 text-xs text-muted-foreground/70">
          No questions — the dialog will show only the title and the bottom buttons.
        </p>
      )}
      {value.map((q, idx) => (
        <PromptQuestionCard
          key={idx}
          question={q}
          idx={idx}
          total={value.length}
          onChange={(next) => update(idx, next)}
          onRemove={() => remove(idx)}
          onMove={(dir) => move(idx, dir)}
        />
      ))}
      <AddPromptQuestionMenu onAdd={add} />
    </div>
  );
}

function AddPromptQuestionMenu({
  onAdd,
}: {
  onAdd: (type: PromptQuestion["type"]) => void;
}) {
  const [open, setOpen] = React.useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className="text-xs">
          <Plus className="h-3.5 w-3.5" />
          Add question
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-56 p-1 border border-white/10 bg-[hsl(240_10%_6%/0.96)] backdrop-blur-xl">
        <button
          type="button"
          onClick={() => { onAdd("text"); setOpen(false); }}
          className="w-full text-left rounded px-2 py-1.5 text-xs text-foreground/80 hover:bg-white/[0.04]"
        >
          Free-text field
        </button>
        <button
          type="button"
          onClick={() => { onAdd("yesno"); setOpen(false); }}
          className="w-full text-left rounded px-2 py-1.5 text-xs text-foreground/80 hover:bg-white/[0.04]"
        >
          Yes / No buttons
        </button>
      </PopoverContent>
    </Popover>
  );
}

function PromptQuestionCard({
  question,
  idx,
  total,
  onChange,
  onRemove,
  onMove,
}: {
  question: PromptQuestion;
  idx: number;
  total: number;
  onChange: (next: PromptQuestion) => void;
  onRemove: () => void;
  onMove: (dir: -1 | 1) => void;
}) {
  return (
    <div className="rounded-lg border border-white/[0.08] bg-white/[0.02] px-3 py-2.5 space-y-2.5">
      <header className="flex items-center gap-2">
        <GripVertical className="h-4 w-4 text-muted-foreground/50" />
        <span className={cn(
          "rounded-md border px-2 py-0.5 text-[11px] font-medium",
          question.type === "text"
            ? "border-sky-400/30 bg-sky-400/10 text-sky-200"
            : "border-emerald-400/30 bg-emerald-400/10 text-emerald-200",
        )}>
          {idx + 1}. {question.type === "text" ? "Free text" : "Yes / No"}
        </span>
        <input
          type="text"
          value={question.key}
          onChange={(e) => onChange({ ...question, key: e.target.value.trim() })}
          placeholder="key"
          className="w-24 rounded-md border border-white/10 bg-white/[0.04] px-2 py-1 text-xs font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          title="Token name for #{prompt.<key>} — lowercase, alphanumeric + underscore."
        />
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
            title="Remove question"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      </header>

      <FieldRow label="Question">
        <input
          type="text"
          value={question.label}
          onChange={(e) => onChange({ ...question, label: e.target.value })}
          placeholder={question.type === "yesno"
            ? "Sales receipt created?"
            : "Add a short summary of what you did"}
          className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
        />
      </FieldRow>

      {question.type === "text" ? (
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={question.required}
            onChange={(e) => onChange({ ...question, required: e.target.checked })}
            className="h-4 w-4 rounded border-white/20 bg-white/[0.04]"
          />
          Must be filled in
        </label>
      ) : (
        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <PromptYesNoButtonField
            label="Positive answer"
            value={question.yes_label}
            placeholder="Yes"
            tone="emerald"
            onChange={(v) => onChange({ ...question, yes_label: v })}
          />
          <PromptYesNoButtonField
            label="Negative answer (cancel)"
            value={question.no_label}
            placeholder="No"
            tone="rose"
            onChange={(v) => onChange({ ...question, no_label: v })}
          />
        </div>
      )}
    </div>
  );
}

/// Single button-label slot for a yes/no question. The text input is
/// active when the button is shown; toggling the checkbox to "hidden"
/// nulls the label so the dialog skips that button. Tone is just a
/// visual cue (green for positive, red for negative).
function PromptYesNoButtonField({
  label,
  value,
  placeholder,
  tone,
  onChange,
}: {
  label: string;
  value: string | null;
  placeholder: string;
  tone: "emerald" | "rose";
  onChange: (next: string | null) => void;
}) {
  const visible = value !== null;
  const accent = tone === "emerald"
    ? "border-emerald-400/30 focus:ring-emerald-400/40"
    : "border-rose-400/30 focus:ring-rose-400/40";
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between gap-2">
        <span className="text-xs font-medium text-muted-foreground">{label}</span>
        <label className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
          <input
            type="checkbox"
            checked={visible}
            onChange={(e) => onChange(e.target.checked ? (value ?? placeholder) : null)}
            className="h-3.5 w-3.5 rounded border-white/20 bg-white/[0.04]"
          />
          {visible ? "Visible" : "Hidden"}
        </label>
      </div>
      <input
        type="text"
        value={value ?? ""}
        disabled={!visible}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className={cn(
          "w-full rounded-md border bg-white/[0.04] px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 disabled:opacity-40",
          visible ? accent : "border-white/10",
        )}
      />
    </div>
  );
}
