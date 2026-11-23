import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { triggerApi, type TriggerListItem } from "@/lib/triggers-api";
import { cn } from "@/lib/utils";
import { formFieldKeysOfType, formFieldOptionLabel, useTriggerFormFields } from "../formFields";

/// v0.1.58 — fixed value or the value an agent filled in on the form.
export function ValueSourceSelect({ fromForm, onChange }: { fromForm: boolean; onChange: (fromForm: boolean) => void }) {
  return (
    <FieldRow label="Value">
      <NativeSelect value={fromForm ? "form" : "fixed"} onChange={(v) => onChange(v === "form")}>
        <option value="fixed">Fixed</option>
        <option value="form">From a form field</option>
      </NativeSelect>
    </FieldRow>
  );
}


/// The keyed form fields of `type` across all templates (Settings → Templates
/// → Forms → field → Trigger key). A saved key no template has any more stays
/// selectable and is flagged.
export function FormFieldKeySelect({ type, label, value, onChange, hint, className }: {
  type: "Agent" | "Queue" | "DateTime";
  label: string;
  value: string;
  onChange: (key: string) => void;
  hint: string;
  className?: string;
}) {
  const options = formFieldKeysOfType(useTriggerFormFields(), type);
  const known = options.some((o) => o.key === value);
  return (
    <FieldRow label={label} className={className}>
      <NativeSelect value={value} onChange={onChange}>
        <option value="">— Select —</option>
        {options.map((o) => (
          <option key={o.key} value={o.key}>{formFieldOptionLabel(o)}</option>
        ))}
        {value && !known && <option value={value}>{value} (not on any form)</option>}
      </NativeSelect>
      <p className="mt-1 text-[11px] text-muted-foreground/70">
        {options.length === 0
          ? `No form has a ${type === "DateTime" ? "date & time" : type.toLowerCase()} field with a trigger key yet. Add one in Settings → Templates → Forms.`
          : hint}
      </p>
    </FieldRow>
  );
}


export function FieldRow({
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


export function NativeSelect({
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
      className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring [&_option]:bg-popover [&_option]:text-popover-foreground"
    >
      {children}
    </select>
  );
}


/// Picker for "when this pending-till expires, run trigger Y". Restricts
/// the dropdown to time:reminder triggers — those are the only ones that
/// semantically belong as a chain target. Empty value = no chain (reminder
/// scan keeps its default cross-join behaviour for this ticket).
export function ChainTriggerPicker({
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

