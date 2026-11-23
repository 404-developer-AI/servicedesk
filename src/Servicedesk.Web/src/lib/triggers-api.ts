import { request } from "@/lib/http";

// ---- Triggers (admin) ----

export type TriggerActivatorKind = "action" | "time" | "manual";
export type TriggerActivatorMode =
  | "selective"
  | "always"
  | "reminder"
  | "escalation"
  | "escalation_warning"
  // v0.0.39 — sole mode for manual triggers today. The trigger runs
  // only when an agent clicks "Create linked X ticket" in the side
  // panel; never fired by the event evaluator or scheduler.
  | "linked_ticket_creator";
export type TriggerRunSummary = {
  applied: number;
  skippedNoMatch: number;
  skippedLoop: number;
  failed: number;
  lastFiredUtc: string | null;
};
export type TriggerListItem = {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  activatorKind: TriggerActivatorKind;
  activatorMode: TriggerActivatorMode;
  locale: string | null;
  timezone: string | null;
  createdUtc: string;
  updatedUtc: string;
  groupId: string | null;
  sortOrder: number;
  runs: TriggerRunSummary;
};
export type TriggerGroup = {
  id: string;
  name: string;
  color: string | null;
  sortOrder: number;
  createdUtc: string;
  updatedUtc: string;
};
export type TriggerGroupInput = {
  name: string;
  color: string | null;
};
export type TriggerPlacement = {
  id: string;
  groupId: string | null;
  sortOrder: number;
};
export type TriggerGroupPlacement = {
  id: string;
  sortOrder: number;
};
export type TriggerListResponse = {
  items: TriggerListItem[];
  runSummaryWindowHours: number;
};
export type TriggerDetail = {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  activatorKind: TriggerActivatorKind;
  activatorMode: TriggerActivatorMode;
  conditionsJson: string;
  actionsJson: string;
  locale: string | null;
  timezone: string | null;
  note: string;
  createdUtc: string;
  updatedUtc: string;
  createdByUserId: string | null;
  // v0.0.39 — only populated for manual triggers; identifies the
  // ticket-type the trigger is bound to.
  manualTicketTypeId?: string | null;
};
export type TriggerInput = {
  name: string;
  description: string;
  isActive: boolean;
  activatorKind: TriggerActivatorKind;
  activatorMode: TriggerActivatorMode;
  conditionsJson: string;
  actionsJson: string;
  locale: string | null;
  timezone: string | null;
  note: string;
  // v0.0.39 — required when activatorKind === "manual"; ignored
  // otherwise. The server validates the pairing.
  manualTicketTypeId?: string | null;
};
export type TriggerConditionField = {
  key: string;
  label: string;
  type: string;
};
export type TriggerTemplateVariable = {
  path: string;
  label: string;
  type: "string" | "datetime";
  example: string;
};
export type TriggerMetadata = {
  conditionFields: TriggerConditionField[];
  conditionOperators: string[];
  actionKinds: string[];
  activatorPairs: string[];
  templateVariables: TriggerTemplateVariable[];
  maxConditionDepth: number;
};
/// v0.1.58 — a form field with a trigger key (`form.field.<key>`), per template.
export type TriggerFormField = {
  templateId: string;
  templateName: string;
  templateIsActive: boolean;
  key: string;
  label: string;
  type: string;
};
export type TriggerRun = {
  id: string;
  triggerId: string;
  ticketId: string;
  ticketNumber: number | null;
  ticketEventId: number | null;
  firedUtc: string;
  outcome: string;
  appliedChangesJson: string | null;
  errorClass: string | null;
  errorMessage: string | null;
};
export type TriggerRunPage = {
  items: TriggerRun[];
  nextCursor: string | null;
};
export type TriggerDryRunActionStatus =
  | "wouldapply"
  | "wouldnoop"
  | "failed"
  | "nohandler";
export type TriggerDryRunAction = {
  kind: string;
  status: TriggerDryRunActionStatus;
  summary: unknown;
  failure: string | null;
};
export type TriggerDryRunResult = {
  matched: boolean;
  failureReason: string | null;
  actions: TriggerDryRunAction[];
};
export const triggerApi = {
  list: () => request<TriggerListResponse>("GET", "/api/admin/triggers"),
  get: (id: string) => request<TriggerDetail>("GET", `/api/admin/triggers/${id}`),
  create: (body: TriggerInput) =>
    request<TriggerDetail>("POST", "/api/admin/triggers", body),
  update: (id: string, body: TriggerInput) =>
    request<TriggerDetail>("PUT", `/api/admin/triggers/${id}`, body),
  setActive: (id: string, isActive: boolean) =>
    request<void>("POST", `/api/admin/triggers/${id}/active`, { isActive }),
  remove: (id: string) =>
    request<void>("DELETE", `/api/admin/triggers/${id}`),
  metadata: () => request<TriggerMetadata>("GET", "/api/admin/triggers/metadata"),
  runs: (id: string, params: { limit?: number; cursorUtc?: string } = {}) => {
    const qs = new URLSearchParams();
    if (params.limit) qs.set("limit", String(params.limit));
    if (params.cursorUtc) qs.set("cursorUtc", params.cursorUtc);
    const tail = qs.toString() ? `?${qs.toString()}` : "";
    return request<TriggerRunPage>("GET", `/api/admin/triggers/${id}/runs${tail}`);
  },
  /** `formId` (optional): simulate a form pass with that form's answers. */
  dryRun: (id: string, ticketId: string, formId?: string | null) =>
    request<TriggerDryRunResult>("POST", `/api/admin/triggers/${id}/dry-run`, { ticketId, formId: formId ?? null }),
  formFields: () =>
    request<{ items: TriggerFormField[] }>("GET", "/api/admin/triggers/form-fields").then((r) => r.items),
  reorder: (items: TriggerPlacement[]) =>
    request<void>("POST", "/api/admin/triggers/reorder", { items }),
};
export const triggerGroupApi = {
  list: () =>
    request<{ items: TriggerGroup[] }>("GET", "/api/admin/trigger-groups"),
  create: (body: TriggerGroupInput) =>
    request<TriggerGroup>("POST", "/api/admin/trigger-groups", body),
  update: (id: string, body: TriggerGroupInput) =>
    request<TriggerGroup>("PUT", `/api/admin/trigger-groups/${id}`, body),
  remove: (id: string) =>
    request<void>("DELETE", `/api/admin/trigger-groups/${id}`),
  reorder: (items: TriggerGroupPlacement[]) =>
    request<void>("POST", "/api/admin/trigger-groups/reorder", { items }),
};
