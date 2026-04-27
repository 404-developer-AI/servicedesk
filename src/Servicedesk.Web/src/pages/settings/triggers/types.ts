// Editor-side shape for a trigger's conditions tree. Mirrors the JSONB
// schema accepted by the matcher (`{ op, items }` or `{ field, operator, value }`).
// The UI normalizes leaves (every value is stored as a string array for
// multi-select operators, single string for the rest) so a switch from
// `is` to `contains_one` doesn't lose data.
export type ConditionLeaf = {
  field: string;
  operator: string;
  value: string | string[] | null;
};

export type ConditionGroup = {
  op: "AND" | "OR" | "NOT";
  items: ConditionNode[];
};

export type ConditionNode = ConditionGroup | ConditionLeaf;

export function isGroup(node: ConditionNode): node is ConditionGroup {
  return (node as ConditionGroup).op !== undefined;
}

export const EMPTY_AND_GROUP: ConditionGroup = { op: "AND", items: [] };

// Editor-side action shape. Stored as a tagged union keyed on `kind`;
// the JSON payload is normalized at submit time (`actionToBackend`) to
// the shape the C# handlers expect, and reversed on load
// (`actionFromBackend`).
//
// `next_trigger_id` is optional on every non-clear mode: when set, the
// scheduler fires that specific trigger (and only that one) when the
// pending-till elapses. UI restricts the picker to time:reminder
// triggers; the BE accepts any UUID and trusts the FK.
export type SetPendingTillAction =
  | { kind: "set_pending_till"; mode: "absolute"; value: string; next_trigger_id?: string | null }
  | { kind: "set_pending_till"; mode: "relative"; value: string; next_trigger_id?: string | null }
  | { kind: "set_pending_till"; mode: "clear" }
  | {
      kind: "set_pending_till";
      mode: "businessDays";
      business_days: number;
      wake_at_local: string;
      next_trigger_id?: string | null;
    };

export type TriggerAction =
  | { kind: "set_queue"; queue_id: string }
  | { kind: "set_priority"; priority_id: string }
  | { kind: "set_status"; status_id: string }
  | { kind: "set_owner"; user_id: string | null }
  | SetPendingTillAction
  | { kind: "add_internal_note"; body_html: string }
  | { kind: "add_public_note"; body_html: string }
  | {
      kind: "send_mail";
      to: string;
      subject: string;
      body_html: string;
      include_triggering_attachments?: boolean;
    };

export const ACTION_KIND_LABELS: Record<TriggerAction["kind"], string> = {
  set_queue: "Set queue",
  set_priority: "Set priority",
  set_status: "Set status",
  set_owner: "Set owner",
  set_pending_till: "Set pending till",
  add_internal_note: "Add internal note",
  add_public_note: "Add public note",
  send_mail: "Send mail",
};

export function blankActionForKind(kind: TriggerAction["kind"]): TriggerAction {
  switch (kind) {
    case "set_queue": return { kind, queue_id: "" };
    case "set_priority": return { kind, priority_id: "" };
    case "set_status": return { kind, status_id: "" };
    case "set_owner": return { kind, user_id: null };
    case "set_pending_till": return { kind, mode: "relative", value: "P1D" };
    case "add_internal_note": return { kind, body_html: "" };
    case "add_public_note": return { kind, body_html: "" };
    case "send_mail":
      return {
        kind,
        to: "customer",
        subject: "",
        body_html: "",
        include_triggering_attachments: false,
      };
  }
}

/// Normalize the editor's tagged-union action to the JSON shape the C#
/// handlers parse. Most action kinds pass through unchanged; only
/// `set_pending_till` needs flattening because the editor groups its
/// four input shapes under a `mode` discriminator while the handler
/// reads top-level `absolute` / `relative` / `businessDays` / `clear`.
export function actionToBackend(action: TriggerAction): Record<string, unknown> {
  if (action.kind !== "set_pending_till") return action as unknown as Record<string, unknown>;
  if (action.mode === "clear") return { kind: "set_pending_till", clear: true };
  const chain = action.next_trigger_id
    ? { nextTriggerId: action.next_trigger_id }
    : {};
  switch (action.mode) {
    case "absolute": return { kind: "set_pending_till", absolute: action.value, ...chain };
    case "relative": return { kind: "set_pending_till", relative: action.value, ...chain };
    case "businessDays":
      return {
        kind: "set_pending_till",
        businessDays: action.business_days,
        wakeAtLocal: action.wake_at_local,
        ...chain,
      };
  }
}

/// Reverse of `actionToBackend`. Other kinds round-trip; set_pending_till
/// is detected by the presence of one of the four BE keys.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function actionFromBackend(raw: any): TriggerAction {
  if (raw?.kind !== "set_pending_till") return raw as TriggerAction;
  if (raw.clear === true) return { kind: "set_pending_till", mode: "clear" };
  const chain: { next_trigger_id?: string | null } = typeof raw.nextTriggerId === "string"
    ? { next_trigger_id: raw.nextTriggerId }
    : {};
  if (typeof raw.absolute === "string")
    return { kind: "set_pending_till", mode: "absolute", value: raw.absolute, ...chain };
  if (typeof raw.relative === "string")
    return { kind: "set_pending_till", mode: "relative", value: raw.relative, ...chain };
  if (typeof raw.businessDays === "number")
    return {
      kind: "set_pending_till",
      mode: "businessDays",
      business_days: raw.businessDays,
      wake_at_local: typeof raw.wakeAtLocal === "string" ? raw.wakeAtLocal : "08:00",
      ...chain,
    };
  // Legacy editor shape (mode/value) — keep round-tripping until any
  // pre-fix triggers are migrated.
  if (raw.mode === "absolute" || raw.mode === "relative") {
    return { kind: "set_pending_till", mode: raw.mode, value: String(raw.value ?? ""), ...chain };
  }
  if (raw.mode === "clear") return { kind: "set_pending_till", mode: "clear" };
  return { kind: "set_pending_till", mode: "relative", value: "P1D", ...chain };
}

export function parseConditions(json: string): ConditionGroup {
  try {
    const parsed = JSON.parse(json);
    if (parsed && typeof parsed === "object" && "op" in parsed && Array.isArray(parsed.items)) {
      return parsed as ConditionGroup;
    }
  } catch { /* fall through */ }
  return { ...EMPTY_AND_GROUP };
}

export function parseActions(json: string): TriggerAction[] {
  try {
    const parsed = JSON.parse(json);
    if (Array.isArray(parsed)) return parsed.map(actionFromBackend);
  } catch { /* fall through */ }
  return [];
}

/// Serialize a list of editor actions to the JSON shape persisted in
/// the `actions` JSONB column.
export function serializeActions(actions: TriggerAction[]): string {
  return JSON.stringify(actions.map(actionToBackend));
}

export function isExpertConditions(group: ConditionGroup): boolean {
  if (group.op !== "AND") return true;
  return group.items.some((item) => isGroup(item));
}
