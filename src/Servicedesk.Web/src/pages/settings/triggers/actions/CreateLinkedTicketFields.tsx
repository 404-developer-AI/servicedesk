import type { ActionTaxonomies } from "./actionEditorTypes";
import { type TriggerAction } from "../types";
import { type TriggerTemplateVariable } from "@/lib/triggers-api";
import { TriggerTemplateField } from "../TriggerTemplateField";
import { AgentPicker } from "@/components/AgentPicker";
import { CompanyPicker } from "@/components/CompanyPicker";
import { ContactPicker } from "@/components/ContactPicker";
import { cn } from "@/lib/utils";
import { FieldRow, NativeSelect } from "./actionFieldPrimitives";

export function CreateLinkedTicketFields({
  action,
  onChange,
  taxonomies,
  variables,
}: {
  action: Extract<TriggerAction, { kind: "create_linked_ticket" }>;
  onChange: (next: TriggerAction) => void;
  taxonomies: ActionTaxonomies;
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
          className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
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

      <div className="rounded-md border border-glass-strong bg-glass p-3">
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
                      : "border-glass bg-glass text-muted-foreground hover:text-foreground",
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
                      : "border-glass bg-glass text-muted-foreground hover:text-foreground",
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

