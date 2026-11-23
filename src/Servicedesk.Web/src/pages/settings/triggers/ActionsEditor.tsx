import * as React from "react";
import { Plus, Trash2, GripVertical } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { ACTION_KIND_LABELS, KNOWN_ACTION_KINDS, blankActionForKind, type KnownActionKind, type TriggerAction } from "./types";
import { type TriggerTemplateVariable } from "@/lib/triggers-api";
import type { ActionTaxonomies } from "./actions/actionEditorTypes";
import { cn } from "@/lib/utils";
import { ActionForm } from "./actions/ActionForm";

type Props = {
  value: TriggerAction[];
  onChange: (next: TriggerAction[]) => void;
  taxonomies: ActionTaxonomies;
  variables: TriggerTemplateVariable[];
  /// v0.0.42 — drives which action kinds the "Add action" menu offers.
  /// Gate triggers expose prompt_confirm only, action/time/manual hide
  /// it. Omitted = all kinds (legacy default for callers that haven't
  /// been threaded through yet).
  activatorKind?: string;
  /// The gate mode ("status_change" | "first_open") when activatorKind is
  /// "gate". Splits gate behaviour: status_change carries exactly one
  /// gate action and nothing else; first_open carries one (seeded)
  /// title_review plus any regular actions that run after confirmation.
  activatorMode?: string;
};


const ACTION_KINDS: KnownActionKind[] = [...KNOWN_ACTION_KINDS];


/// Status-change gates may only carry one of the two status-gate actions.
const GATE_ACTION_KINDS: KnownActionKind[] = ["prompt_confirm", "require_contact_company"];


/// Every gate-only action kind — hidden from non-gate activators so the
/// admin cannot save a payload the BE validator will reject.
function isGateAction(kind: string): boolean {
  return kind === "prompt_confirm" || kind === "require_contact_company" || kind === "title_review";
}


function allowedKindsFor(activatorKind?: string, activatorMode?: string): KnownActionKind[] {
  if (activatorKind === "gate") {
    // First-open gates allow regular actions alongside the seeded
    // title_review (which isn't re-addable); status-change gates offer
    // only the two status-gate actions.
    if (activatorMode === "first_open") return ACTION_KINDS.filter((k) => !isGateAction(k));
    return GATE_ACTION_KINDS;
  }
  return ACTION_KINDS.filter((k) => !isGateAction(k));
}


/// Renders the per-trigger actions list. Each entry is a typed action
/// block with its own inline form fragment — picking <c>set_priority</c>
/// shows a priority dropdown; <c>send_mail</c> shows recipient + subject +
/// HTML body fields with the variable picker in scope. Order matters: the
/// dispatcher applies actions top-to-bottom so admins can chain "set
/// priority then add a note explaining why".
export function ActionsEditor({ value, onChange, taxonomies, variables, activatorKind, activatorMode }: Props) {
  const addableKinds = allowedKindsFor(activatorKind, activatorMode);
  // Status-change gates hide the add menu once their single gate action is
  // present; first-open gates keep it open so the admin can add follow-up
  // actions (e.g. the approval note) after the seeded title_review.
  const hideAdd = activatorKind === "gate"
    && activatorMode !== "first_open"
    && value.some((a) => isGateAction(a.kind));
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
        <p className="rounded-md border border-dashed border-glass-strong bg-glass px-3 py-3 text-xs text-muted-foreground/70">
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
      <PopoverContent className="w-56 p-1 border border-glass bg-popover/95 backdrop-blur-xl">
        {kinds.map((k) => (
          <button
            key={k}
            type="button"
            onClick={() => { onAdd(k); setOpen(false); }}
            className="w-full text-left rounded px-2 py-1.5 text-xs text-foreground/80 hover:bg-glass-hover"
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
  taxonomies: ActionTaxonomies;
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
        ? "border-amber-400/30 bg-amber-400/4"
        : "border-glass-strong bg-glass",
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

