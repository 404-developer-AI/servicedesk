import { Switch } from "@/components/ui/switch";
import { formInputCn } from "@/components/forms/FormFieldInput";
import type { FormField, FormOutlookMapping } from "@/lib/forms-api";
import { MAPPING_KEYS, ROWS, outlookProblems, rowCandidates, type MappingKey } from "./outlookMapping";
import { cn } from "@/lib/utils";

/// v0.1.61 — Settings → Templates → Forms: the "Outlook appointment" block.
/// Maps fields of the form onto the properties of the calendar appointment.
/// The server re-validates everything (FormOutlookMapping.Validate).

export function OutlookMappingCard({
  value,
  fields,
  onChange,
}: {
  value: FormOutlookMapping;
  fields: FormField[];
  onChange: (next: FormOutlookMapping) => void;
}) {
  const set = (key: MappingKey, id: string) => {
    let next: FormOutlookMapping = { ...value, [key]: id || null };
    if (!id) {
      // A cleared field takes the properties that depend on it along.
      if (key === "teamsFieldId") next = { ...next, inviteesFieldId: null, messageFieldId: null, shareAttachmentsFieldId: null };
      if (key === "inviteesFieldId") next = { ...next, messageFieldId: null, shareAttachmentsFieldId: null };
      if (key === "attachmentsFieldId") next = { ...next, shareAttachmentsFieldId: null };
    }
    onChange(next);
  };

  const visible = (key: MappingKey) => {
    if (key === "inviteesFieldId") return !!value.teamsFieldId;
    if (key === "messageFieldId") return !!value.inviteesFieldId;
    if (key === "shareAttachmentsFieldId") return !!value.attachmentsFieldId && !!value.inviteesFieldId;
    return true;
  };

  const used = new Set(MAPPING_KEYS.map((k) => value[k]).filter((x): x is string => !!x));
  const problems = outlookProblems(value, fields);

  return (
    <section className="glass-card space-y-4 p-5">
      <div className="space-y-1">
        <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">Outlook appointment</h2>
        <p className="text-xs text-muted-foreground/70">
          When the form is saved, the appointment is created in the chosen technicians' Outlook calendars — one per
          technician, or one Teams meeting with an organizer. Needs Settings → Microsoft 365 → Calendar planning to be on.
        </p>
      </div>
      <label className="flex items-center gap-3">
        <Switch checked={value.enabled} onCheckedChange={(v) => onChange({ ...value, enabled: v })} />
        <span className="text-sm">Create Outlook appointments</span>
      </label>

      {value.enabled && (
        <>
          <div className="grid gap-3 sm:grid-cols-2">
            {ROWS.filter((r) => visible(r.key)).map((r) => {
              const candidates = rowCandidates(r, fields, value);
              return (
                <label key={r.key} className="block space-y-1">
                  <span className="text-[11px] font-medium text-muted-foreground">
                    {r.label}{r.required && <span className="text-rose-600 dark:text-rose-400"> *</span>}
                  </span>
                  <select value={value[r.key] ?? ""} onChange={(e) => set(r.key, e.target.value)}
                    className={cn(formInputCn, "h-9")}>
                    <option value="">Not used</option>
                    {candidates.map((f) => (
                      <option key={f.id} value={f.id} disabled={used.has(f.id) && value[r.key] !== f.id}>
                        {f.label || "Untitled"}{f.type === "Duration" ? " (start + duration)" : ""}
                      </option>
                    ))}
                  </select>
                </label>
              );
            })}
          </div>
          {problems.length > 0 && (
            <ul className="space-y-0.5">
              {problems.map((p) => <li key={p} className="text-[11px] text-muted-foreground">{p}</li>)}
            </ul>
          )}
          <p className="text-[11px] text-muted-foreground/70">
            For external people the appointment text is the message for attendees, never the internal form note.
            Attachments go to them only when 'Share attachments' is ticked on the form.
          </p>
        </>
      )}
    </section>
  );
}
