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

// v0.0.39 — create_linked_ticket. Carries the full preset payload:
// subject/body templates with #{ticket.*} tokens, defaults for status/
// queue/priority/category/assignee, three-way enum for the requester
// and company source, and an optional initial-note sub-block.
export type CreateLinkedTicketAction = {
  kind: "create_linked_ticket";
  subject_template: string;
  body_html_template: string;
  queue_id: string | null;
  status_id: string | null;
  priority_id: string | null;
  category_id: string | null;
  assignee_user_id: string | null;
  requester_source: "parent" | "fixed_contact" | "current_agent";
  requester_contact_id: string | null;
  company_source: "parent" | "from_requester_primary" | "fixed_company";
  company_id: string | null;
  initial_note: {
    body_html_template: string;
    is_internal: boolean;
  } | null;
};

// v0.0.42 — prompt_confirm. Single action allowed on a
// gate:status_change trigger. The dialog renders title + an optional
// message + a list of inline questions (free-text or yes/no buttons,
// each side independently hide-able). Each question carries an admin-
// defined `key` referenced from `note_template` as `#{prompt.<key>}`.
// The status change is gated until every required question is
// satisfied and the agent clicks the confirm button.
export type PromptQuestion =
  | {
      key: string;
      type: "text";
      label: string;
      required: boolean;
    }
  | {
      key: string;
      type: "yesno";
      label: string;
      yes_label: string | null;
      no_label: string | null;
    };

export type PromptConfirmAction = {
  kind: "prompt_confirm";
  to_status_id: string;
  from_status_id: string | null;
  title: string;
  /// When false, the dialog skips the message section entirely (button-
  /// only gate). The `message` field is kept around so toggling the
  /// switch back on restores the previously-typed body.
  show_message: boolean;
  message: string;
  questions: PromptQuestion[];
  confirm_label: string;
  cancel_label: string;
  note_visibility: "internal" | "public";
  note_template: string;
};

export type TriggerAction =
  | { kind: "set_queue"; queue_id: string }
  | { kind: "set_priority"; priority_id: string }
  | { kind: "set_status"; status_id: string }
  | { kind: "set_owner"; user_id: string | null }
  | SetPendingTillAction
  | { kind: "add_internal_note"; body_html: string }
  | { kind: "add_public_note"; body_html: string }
  | { kind: "repost_as_public_reply" }
  | {
      kind: "send_mail";
      to: string;
      subject: string;
      body_html: string;
    }
  | {
      kind: "send_survey";
      survey_id: string;
      ttl_days_override?: number | null;
      recipient_override?: string | null;
    }
  | CreateLinkedTicketAction
  | PromptConfirmAction
  // Sentinel for actions whose `kind` this editor build doesn't know
  // about (e.g. a future kind saved by a newer frontend or hand-
  // written JSON). Carries the original payload verbatim so the admin
  // doesn't lose work; rendered read-only in the editor and rejected
  // by the BE validator on save until the admin removes or replaces
  // the entry.
  | { kind: "__unknown"; original_kind: string; raw: Record<string, unknown> };

export const KNOWN_ACTION_KINDS = [
  "set_queue",
  "set_priority",
  "set_status",
  "set_owner",
  "set_pending_till",
  "add_internal_note",
  "add_public_note",
  "repost_as_public_reply",
  "send_mail",
  "send_survey",
  "create_linked_ticket",
  "prompt_confirm",
] as const;

export type KnownActionKind = (typeof KNOWN_ACTION_KINDS)[number];

export const ACTION_KIND_LABELS: Record<KnownActionKind, string> = {
  set_queue: "Set queue",
  set_priority: "Set priority",
  set_status: "Set status",
  set_owner: "Set owner",
  set_pending_till: "Set pending till",
  add_internal_note: "Add internal note",
  add_public_note: "Add public note",
  repost_as_public_reply: "Repost as public reply",
  send_mail: "Send mail",
  send_survey: "Send survey",
  create_linked_ticket: "Create linked ticket",
  prompt_confirm: "Confirmation dialog",
};

export function blankActionForKind(kind: KnownActionKind): TriggerAction {
  switch (kind) {
    case "set_queue": return { kind, queue_id: "" };
    case "set_priority": return { kind, priority_id: "" };
    case "set_status": return { kind, status_id: "" };
    case "set_owner": return { kind, user_id: null };
    case "set_pending_till": return { kind, mode: "relative", value: "P1D" };
    case "add_internal_note": return { kind, body_html: "" };
    case "add_public_note": return { kind, body_html: "" };
    case "repost_as_public_reply": return { kind };
    case "send_mail":
      return {
        kind,
        to: "customer",
        subject: "",
        body_html: "",
      };
    case "send_survey":
      return { kind, survey_id: "" };
    case "create_linked_ticket":
      return {
        kind,
        subject_template: "",
        body_html_template: "",
        queue_id: null,
        status_id: null,
        priority_id: null,
        category_id: null,
        assignee_user_id: null,
        requester_source: "parent",
        requester_contact_id: null,
        company_source: "parent",
        company_id: null,
        initial_note: null,
      };
    case "prompt_confirm":
      return {
        kind,
        to_status_id: "",
        from_status_id: null,
        title: "",
        show_message: false,
        message: "",
        questions: [],
        confirm_label: "Yes, completed",
        cancel_label: "Cancel",
        note_visibility: "internal",
        note_template: "",
      };
  }
}

/// Normalize the editor's tagged-union action to the JSON shape the C#
/// handlers parse. Most action kinds pass through unchanged; only
/// `set_pending_till` needs flattening because the editor groups its
/// four input shapes under a `mode` discriminator while the handler
/// reads top-level `absolute` / `relative` / `businessDays` / `clear`.
export function actionToBackend(action: TriggerAction): Record<string, unknown> {
  if (action.kind === "__unknown") return action.raw;
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
/// is detected by the presence of one of the four BE keys. Anything we
/// don't recognise is wrapped as `__unknown` so the editor can warn the
/// admin instead of silently coercing the entry to a different kind.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function actionFromBackend(raw: any): TriggerAction {
  if (!raw || typeof raw !== "object") {
    return { kind: "__unknown", original_kind: "(invalid)", raw: {} };
  }
  if (typeof raw.kind !== "string"
      || !(KNOWN_ACTION_KINDS as readonly string[]).includes(raw.kind)) {
    return {
      kind: "__unknown",
      original_kind: typeof raw.kind === "string" ? raw.kind : "(missing)",
      raw,
    };
  }
  if (raw.kind === "prompt_confirm") {
    return normalizePromptConfirm(raw);
  }
  if (raw.kind !== "set_pending_till") return raw as TriggerAction;
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

/// Round-trips a prompt_confirm action from the BE-stored shape to the
/// editor shape. Migrates the pre-questions v0.0.42 layout
/// (prompt_label / prompt_required) into a single text question with
/// key="answer" so legacy gates render correctly and existing
/// `#{prompt.answer}` tokens keep resolving after re-save.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function normalizePromptConfirm(raw: any): PromptConfirmAction {
  const questions: PromptQuestion[] = Array.isArray(raw.questions)
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    ? (raw.questions as any[]).flatMap((q): PromptQuestion[] => {
        if (!q || typeof q !== "object") return [];
        const key = typeof q.key === "string" ? q.key : "";
        const label = typeof q.label === "string" ? q.label : "";
        if (!key) return [];
        if (q.type === "yesno") {
          return [{
            key,
            type: "yesno",
            label,
            yes_label: typeof q.yes_label === "string" ? q.yes_label : null,
            no_label: typeof q.no_label === "string" ? q.no_label : null,
          }];
        }
        return [{
          key,
          type: "text",
          label,
          required: q.required === true,
        }];
      })
    : [];
  // Legacy migration: pre-questions gates carry prompt_label /
  // prompt_required at the action root. Synthesize one text question
  // with key="answer" so existing #{prompt.answer} tokens keep working.
  if (questions.length === 0
      && typeof raw.prompt_label === "string"
      && raw.prompt_label.trim().length > 0) {
    questions.push({
      key: "answer",
      type: "text",
      label: raw.prompt_label,
      required: raw.prompt_required === true,
    });
  }
  const message = typeof raw.message === "string" ? raw.message : "";
  const showMessage = typeof raw.show_message === "boolean"
    ? raw.show_message
    : message.trim().length > 0;
  return {
    kind: "prompt_confirm",
    to_status_id: typeof raw.to_status_id === "string" ? raw.to_status_id : "",
    from_status_id: typeof raw.from_status_id === "string" ? raw.from_status_id : null,
    title: typeof raw.title === "string" ? raw.title : "",
    show_message: showMessage,
    message,
    questions,
    confirm_label: typeof raw.confirm_label === "string" ? raw.confirm_label : "Yes, completed",
    cancel_label: typeof raw.cancel_label === "string" ? raw.cancel_label : "Cancel",
    note_visibility: raw.note_visibility === "public" ? "public" : "internal",
    note_template: typeof raw.note_template === "string" ? raw.note_template : "",
  };
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
