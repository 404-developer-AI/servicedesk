import type { ActionTaxonomies } from "./actionEditorTypes";
import { type TriggerAction } from "../types";
import { type TriggerTemplateVariable } from "@/lib/triggers-api";
import { TriggerTemplateField } from "../TriggerTemplateField";
import { FieldRow, NativeSelect } from "./actionFieldPrimitives";
import { PromptQuestionsEditor } from "./PromptQuestionEditors";

/// Form fragment for the prompt_confirm action — the single action
/// allowed on a gate:status_change trigger. Renders the source/target
/// status pickers, an optional message body, the questions list editor,
/// the bottom-button labels and the note template + visibility
/// selector. The to_status_id is required; from_status_id is optional
/// ("from any" when empty).
export function PromptConfirmFields({
  action,
  onChange,
  taxonomies,
  variables,
}: {
  action: Extract<TriggerAction, { kind: "prompt_confirm" }>;
  onChange: (next: TriggerAction) => void;
  taxonomies: ActionTaxonomies;
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
          className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
        />
      </FieldRow>

      <FieldRow label="Message">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={action.show_message}
            onChange={(e) => onChange({ ...action, show_message: e.target.checked })}
            className="h-4 w-4 rounded border-glass-strong bg-glass"
          />
          Show a message above the questions
        </label>
        {action.show_message && (
          <textarea
            value={action.message}
            onChange={(e) => onChange({ ...action, message: e.target.value })}
            rows={3}
            placeholder="Did you complete every required step before closing?"
            className="mt-2 w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
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
            className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          />
        </FieldRow>
        <FieldRow label="Cancel button label">
          <input
            type="text"
            value={action.cancel_label}
            onChange={(e) => onChange({ ...action, cancel_label: e.target.value })}
            placeholder="Cancel"
            className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
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


/// Form fragment for the require_contact_company action — second gate-
/// only action. Hard-blocks a status change when the ticket's requester
/// has no contact_companies row. The dialog itself renders a company
/// picker + role selector (primary/secondary/supplier); the admin
/// configures the source/target status pair, the dialog labels and an
/// optional confirmation note. Tokens available in the note template:
/// <c>#{company.name}</c>, <c>#{company.code}</c>, <c>#{company.role}</c>,
/// <c>#{company.id}</c>, <c>#{agent.name}</c>.
export function RequireContactCompanyFields({
  action,
  onChange,
  taxonomies,
  variables,
}: {
  action: Extract<TriggerAction, { kind: "require_contact_company" }>;
  onChange: (next: TriggerAction) => void;
  taxonomies: ActionTaxonomies;
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
          placeholder="Link company first"
          className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
        />
      </FieldRow>

      <FieldRow label="Message">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={action.show_message}
            onChange={(e) => onChange({ ...action, show_message: e.target.checked })}
            className="h-4 w-4 rounded border-glass-strong bg-glass"
          />
          Show a message above the picker
        </label>
        {action.show_message && (
          <textarea
            value={action.message}
            onChange={(e) => onChange({ ...action, message: e.target.value })}
            rows={3}
            placeholder="This contact is not linked to a company. Pick a company and a role to continue."
            className="mt-2 w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          />
        )}
      </FieldRow>

      <p className="rounded-md border border-glass-strong bg-glass px-3 py-2 text-[11px] text-muted-foreground/80">
        The dialog renders an inline company picker and a primary / secondary / supplier role selector. The status change is blocked until the agent picks a company and a role; the link is created inside the same PATCH that flips the status, so the matcher will not re-fire on a follow-up edit.
      </p>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <FieldRow label="Confirm button label">
          <input
            type="text"
            value={action.confirm_label}
            onChange={(e) => onChange({ ...action, confirm_label: e.target.value })}
            placeholder="Link & continue"
            className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          />
        </FieldRow>
        <FieldRow label="Cancel button label">
          <input
            type="text"
            value={action.cancel_label}
            onChange={(e) => onChange({ ...action, cancel_label: e.target.value })}
            placeholder="Cancel"
            className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
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
          placeholder="Linked #{company.name} as #{company.role} by #{agent.name}"
        />
        <p className="mt-1 text-[11px] text-muted-foreground/70">
          Use <code className="font-mono">#{"{company.name}"}</code>, <code className="font-mono">#{"{company.code}"}</code>, <code className="font-mono">#{"{company.role}"}</code> and <code className="font-mono">#{"{company.id}"}</code> to reference the just-created link, plus <code className="font-mono">#{"{agent.name}"}</code> for the confirming agent. Leave the template empty to skip the note entirely.
        </p>
      </FieldRow>
    </div>
  );
}

