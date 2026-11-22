import type { FormField, FormFieldType, FormOutlookMapping } from "@/lib/forms-api";

/// Outlook appointment mapping helpers (the server re-validates everything).

export type MappingKey = Exclude<keyof FormOutlookMapping, "enabled">;

export const MAPPING_KEYS: MappingKey[] = [
  "titleFieldId", "startFieldId", "endFieldId", "techniciansFieldId", "addressFieldId",
  "teamsFieldId", "inviteesFieldId", "messageFieldId", "attachmentsFieldId", "shareAttachmentsFieldId",
];

export const ROWS: { key: MappingKey; label: string; types: FormFieldType[]; required?: boolean }[] = [
  { key: "titleFieldId", label: "Title", types: ["ShortText"], required: true },
  { key: "startFieldId", label: "Start", types: ["DateTime"], required: true },
  // The end: a date and time, or a duration that counts from the start.
  { key: "endFieldId", label: "End", types: ["DateTime", "Duration"], required: true },
  { key: "techniciansFieldId", label: "Technicians", types: ["Agent"], required: true },
  { key: "addressFieldId", label: "Address", types: ["Address"] },
  { key: "teamsFieldId", label: "Teams meeting", types: ["Checkbox"] },
  { key: "inviteesFieldId", label: "People to invite", types: ["Invitees"] },
  { key: "messageFieldId", label: "Message for attendees", types: ["LongText"] },
  { key: "attachmentsFieldId", label: "Attachments", types: ["Attachments"] },
  { key: "shareAttachmentsFieldId", label: "Share attachments with attendees", types: ["Checkbox"] },
];

/// Fields that can feed a mapping row. A duration only ends the appointment
/// when it counts from the chosen start field.
export function rowCandidates(row: (typeof ROWS)[number], fields: FormField[], m: FormOutlookMapping): FormField[] {
  return fields.filter((f) => row.types.includes(f.type)
    && (f.type !== "Duration" || (!!m.startFieldId && f.durationFrom === m.startFieldId)));
}

export const emptyOutlook: FormOutlookMapping = { enabled: false };

export const outlookIsEmpty = (m: FormOutlookMapping) =>
  !m.enabled && MAPPING_KEYS.every((k) => !m[k]);

/// Drops mapping properties whose field was removed or changed type.
export function pruneOutlook(m: FormOutlookMapping, fields: FormField[]): FormOutlookMapping {
  const typeById = new Map(fields.map((f) => [f.id, f.type]));
  let next = m;
  for (const row of ROWS) {
    const id = m[row.key];
    const type = id ? typeById.get(id) : undefined;
    if (id && (!type || !row.types.includes(type))) next = { ...next, [row.key]: null };
  }
  return next;
}

/// Mirrors the server rules; null when the mapping is fine (or switched off).
export function outlookProblems(m: FormOutlookMapping, fields: FormField[]): string[] {
  if (!m.enabled) return [];
  const out: string[] = [];
  const missing = ROWS.filter((r) => r.required && !m[r.key]).map((r) => r.label.toLowerCase());
  if (missing.length > 0) out.push(`Choose the ${missing.join(", ")} field${missing.length === 1 ? "" : "s"}.`);
  if (m.startFieldId && m.startFieldId === m.endFieldId) out.push("Start and end must be two different fields.");
  if (m.inviteesFieldId && !m.teamsFieldId) out.push("People to invite needs a Teams meeting field.");
  const end = m.endFieldId ? fields.find((f) => f.id === m.endFieldId) : undefined;
  if (end?.type === "Duration" && end.durationFrom !== m.startFieldId)
    out.push("The duration used as the end must count from the start field.");
  return out;
}

