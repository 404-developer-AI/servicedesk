import { useQuery } from "@tanstack/react-query";
import { triggerApi, type TriggerConditionField, type TriggerFormField } from "@/lib/api";

/// v0.1.58 — form fields with a trigger key, as the trigger editor uses them:
/// `form.field.<key>` conditions and "from form field" action values. The same
/// key on several templates is one entry (the trigger addresses the key; the
/// template condition decides which form).

export const FORM_FIELD_PREFIX = "form.field.";

/// Field type the condition editor renders for `form.field.*` (presence and
/// change operators only — no value input).
export const FORM_FIELD_CONDITION_TYPE = "form-field";

export type FormFieldKeyOption = {
  key: string;
  label: string;
  /** Field types this key has across templates (normally one). */
  types: string[];
  templates: string[];
};

export function useTriggerFormFields(): TriggerFormField[] {
  const q = useQuery({ queryKey: ["trigger-form-fields"], queryFn: triggerApi.formFields, staleTime: 60_000 });
  return q.data ?? [];
}

export function groupFormFieldKeys(rows: TriggerFormField[]): FormFieldKeyOption[] {
  const byKey = new Map<string, FormFieldKeyOption>();
  for (const r of rows) {
    const cur = byKey.get(r.key) ?? { key: r.key, label: r.label, types: [], templates: [] };
    if (!cur.types.includes(r.type)) cur.types.push(r.type);
    if (!cur.templates.includes(r.templateName)) cur.templates.push(r.templateName);
    byKey.set(r.key, cur);
  }
  return [...byKey.values()].sort((a, b) => a.key.localeCompare(b.key));
}

/// Keys usable for an action that needs a field of `type` (Agent, Queue, DateTime).
export function formFieldKeysOfType(rows: TriggerFormField[], type: string): FormFieldKeyOption[] {
  return groupFormFieldKeys(rows).filter((o) => o.types.includes(type));
}

export function formFieldOptionLabel(o: FormFieldKeyOption): string {
  return `${o.label} (${o.key}) · ${o.templates.join(", ")}`;
}

/// Condition-field catalog entries for the keyed form fields.
export function formFieldConditionEntries(rows: TriggerFormField[]): TriggerConditionField[] {
  return groupFormFieldKeys(rows).map((o) => ({
    key: FORM_FIELD_PREFIX + o.key,
    label: `Form field: ${o.label} (${o.key})`,
    type: FORM_FIELD_CONDITION_TYPE,
  }));
}
