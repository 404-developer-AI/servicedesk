import type { ActionTaxonomies } from "./actionEditorTypes";
import { type TriggerAction } from "../types";
import { type TriggerTemplateVariable } from "@/lib/triggers-api";
import { TriggerTemplateField } from "../TriggerTemplateField";
import { ValueSourceSelect, FormFieldKeySelect, FieldRow, NativeSelect, ChainTriggerPicker } from "./actionFieldPrimitives";
import { TitleReviewFields, NotifyAgentFields, HandleBackofficeFormFields, SendSurveyFields } from "./ActionKindFields";
import { CreateLinkedTicketFields } from "./CreateLinkedTicketFields";
import { PromptConfirmFields, RequireContactCompanyFields } from "./PromptGateFields";

export function ActionForm({
  action,
  peerActions,
  onChange,
  taxonomies,
  variables,
}: {
  action: TriggerAction;
  peerActions: TriggerAction[];
  onChange: (a: TriggerAction) => void;
  taxonomies: ActionTaxonomies;
  variables: TriggerTemplateVariable[];
}) {
  switch (action.kind) {
    case "__unknown":
      return (
        <div className="space-y-2">
          <p className="text-xs text-amber-200/80">
            This action's kind is not recognised by this editor build. Save will be rejected by the server until you remove or replace the entry. The original payload is preserved below.
          </p>
          <pre className="rounded-md border border-glass-strong bg-glass px-3 py-2 text-[11px] font-mono text-muted-foreground/80 overflow-x-auto">
            {JSON.stringify(action.raw, null, 2)}
          </pre>
        </div>
      );
    case "set_queue":
      return (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <ValueSourceSelect
            fromForm={action.form_field != null}
            onChange={(fromForm) => onChange({ kind: "set_queue", queue_id: "", form_field: fromForm ? "" : null })}
          />
          {action.form_field != null ? (
            <FormFieldKeySelect type="Queue" label="Queue field" className="sm:col-span-2"
              value={action.form_field} onChange={(v) => onChange({ ...action, form_field: v })}
              hint="The queue the agent picked on the form (only queues that agent can access)." />
          ) : (
            <FieldRow label="Queue" className="sm:col-span-2">
              <NativeSelect
                value={action.queue_id}
                onChange={(v) => onChange({ ...action, queue_id: v })}
              >
                <option value="">— Select —</option>
                {taxonomies.queues.map((q) => (
                  <option key={q.id} value={q.id}>{q.name}</option>
                ))}
              </NativeSelect>
            </FieldRow>
          )}
        </div>
      );
    case "set_priority":
      return (
        <FieldRow label="Priority">
          <NativeSelect
            value={action.priority_id}
            onChange={(v) => onChange({ ...action, priority_id: v })}
          >
            <option value="">— Select —</option>
            {taxonomies.priorities.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </NativeSelect>
        </FieldRow>
      );
    case "set_status": {
      // v0.0.40 polish — when this trigger also flips the queue,
      // filter the status dropdown to that target queue's allowed-list.
      // Surfaces the same constraint the runtime enforces: you can't
      // save a `set_status` that the target queue would reject.
      const peerSetQueue = peerActions.find((a): a is Extract<TriggerAction, { kind: "set_queue" }> => a.kind === "set_queue");
      const targetQueue = peerSetQueue
        ? taxonomies.queues.find((q) => q.id === peerSetQueue.queue_id)
        : null;
      const targetAllowed = targetQueue?.allowedStatusIds ?? [];
      const scopedStatuses = targetAllowed.length === 0
        ? taxonomies.statuses
        : taxonomies.statuses.filter((s) => targetAllowed.includes(s.id));
      return (
        <FieldRow label="Status">
          <NativeSelect
            value={action.status_id}
            onChange={(v) => onChange({ ...action, status_id: v })}
          >
            <option value="">— Select —</option>
            {scopedStatuses.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </NativeSelect>
          {targetQueue && targetAllowed.length > 0 && (
            <p className="mt-1 text-[11px] text-muted-foreground/70">
              Filtered to the {targetQueue.name} queue's allowed statuses (set_queue is also active in this trigger).
            </p>
          )}
        </FieldRow>
      );
    }
    case "set_category":
      return (
        <FieldRow label="Category">
          <NativeSelect
            value={action.category_id ?? ""}
            onChange={(v) => onChange({ ...action, category_id: v || null })}
          >
            <option value="">Clear category</option>
            {(taxonomies.categories ?? []).map((c) => (
              <option key={c.id} value={c.id}>{c.name}{!c.isActive ? " (inactive)" : ""}</option>
            ))}
          </NativeSelect>
        </FieldRow>
      );
    case "set_owner":
      return (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <ValueSourceSelect
            fromForm={action.form_field != null}
            onChange={(fromForm) => onChange({ kind: "set_owner", user_id: null, form_field: fromForm ? "" : null })}
          />
          {action.form_field != null ? (
            <FormFieldKeySelect type="Agent" label="Agent field" className="sm:col-span-2"
              value={action.form_field} onChange={(v) => onChange({ ...action, form_field: v })}
              hint="The agent picked on the form. Skipped (and logged) when that agent cannot open the ticket's queue." />
          ) : (
            <FieldRow label="Owner" className="sm:col-span-2">
              <input
                type="text"
                placeholder="agent user-id (Guid) — leave empty to clear"
                value={action.user_id ?? ""}
                onChange={(e) =>
                  onChange({ ...action, user_id: e.target.value.trim() || null })
                }
                className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
              />
            </FieldRow>
          )}
        </div>
      );
    case "set_pending_till": {
      const onModeChange = (next: "formField" | "absolute" | "relative" | "businessDays" | "clear") => {
        switch (next) {
          case "formField":
            onChange({ kind: "set_pending_till", mode: "formField", form_field: "" });
            break;
          case "absolute":
            onChange({ kind: "set_pending_till", mode: "absolute", value: "" });
            break;
          case "relative":
            onChange({ kind: "set_pending_till", mode: "relative", value: "P1D" });
            break;
          case "businessDays":
            onChange({
              kind: "set_pending_till",
              mode: "businessDays",
              business_days: 2,
              wake_at_local: "08:00",
            });
            break;
          case "clear":
            onChange({ kind: "set_pending_till", mode: "clear" });
            break;
        }
      };
      return (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <FieldRow label="Mode">
            <NativeSelect
              value={action.mode}
              onChange={(v) => onModeChange(v as "formField" | "absolute" | "relative" | "businessDays" | "clear")}
            >
              <option value="formField">From a form field (date &amp; time)</option>
              <option value="relative">Relative (ISO 8601 duration)</option>
              <option value="businessDays">Business days at fixed time</option>
              <option value="absolute">Absolute (ISO 8601)</option>
              <option value="clear">Clear</option>
            </NativeSelect>
          </FieldRow>
          {action.mode === "formField" && (
            <FormFieldKeySelect type="DateTime" label="Date & time field" className="sm:col-span-2"
              value={action.form_field} onChange={(v) => onChange({ ...action, form_field: v })}
              hint="Needs a Pending status on the ticket: put a Set status action before this one." />
          )}
          {action.mode === "absolute" && (
            <FieldRow label="Timestamp" className="sm:col-span-2">
              <input
                type="text"
                value={action.value}
                onChange={(e) => onChange({ ...action, value: e.target.value })}
                placeholder="2026-04-30T17:00:00Z"
                className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
              />
            </FieldRow>
          )}
          {action.mode === "relative" && (
            <FieldRow label="Duration" className="sm:col-span-2">
              <input
                type="text"
                value={action.value}
                onChange={(e) => onChange({ ...action, value: e.target.value })}
                placeholder="P1D, PT4H, P3DT12H…"
                className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm font-mono text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
              />
            </FieldRow>
          )}
          {action.mode === "businessDays" && (
            <>
              <FieldRow label="Business days">
                <input
                  type="number"
                  min={0}
                  max={365}
                  step={1}
                  value={action.business_days}
                  onChange={(e) =>
                    onChange({
                      ...action,
                      business_days: Math.max(0, Number.parseInt(e.target.value || "0", 10) || 0),
                    })
                  }
                  className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm font-mono text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring"
                />
              </FieldRow>
              <FieldRow label="Wake-up time (local)">
                <input
                  type="time"
                  value={action.wake_at_local}
                  onChange={(e) => onChange({ ...action, wake_at_local: e.target.value || "08:00" })}
                  className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm font-mono text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring scheme-dark"
                />
              </FieldRow>
              <FieldRow label="Schedule" className="sm:col-span-3">
                <p className="rounded-md border border-glass-strong bg-glass px-3 py-2 text-[11px] text-muted-foreground/80">
                  Uses the default business-hours schedule from <span className="text-foreground/80">Settings → SLA</span>. Working days, holidays and timezone are taken from there.
                </p>
              </FieldRow>
            </>
          )}
          {action.mode !== "clear" && (
            <FieldRow label="When this expires, run" className="sm:col-span-3">
              <ChainTriggerPicker
                value={action.next_trigger_id ?? ""}
                onChange={(v) =>
                  onChange({ ...action, next_trigger_id: v || null })
                }
              />
            </FieldRow>
          )}
        </div>
      );
    }
    case "add_internal_note":
    case "add_public_note":
      return (
        <FieldRow label="Body">
          <TriggerTemplateField
            mode="html"
            variables={variables}
            value={action.body_html}
            onChange={(v) => onChange({ ...action, body_html: v })}
            placeholder="Note body — supports #{variables}"
          />
        </FieldRow>
      );
    case "repost_as_public_reply":
      return (
        <p className="rounded-md border border-glass-strong bg-glass px-3 py-2 text-[11px] text-muted-foreground/80">
          Duplicates the triggering article verbatim as a public reply
          (same body, formatting, and attachments-link). Pair with an{" "}
          <span className="text-foreground/80">Article is internal = true</span>{" "}
          condition to prevent the public copy from re-firing the trigger.
        </p>
      );
    case "send_mail":
      return (
        <div className="space-y-3">
          <FieldRow label="To">
            <NativeSelect
              value={action.to.startsWith("address:") ? "address" : action.to}
              onChange={(v) =>
                onChange({
                  ...action,
                  to: v === "address" ? "address:" : v,
                })
              }
            >
              <option value="customer">Customer</option>
              <option value="owner-agent">Owner agent</option>
              <option value="queue-agents">Queue agents</option>
              <option value="address">Specific address(es)…</option>
            </NativeSelect>
          </FieldRow>
          {action.to.startsWith("address:") && (
            <FieldRow label="Address(es)">
              <input
                type="text"
                value={action.to.slice("address:".length)}
                placeholder="alex@example.com, team@example.com"
                onChange={(e) =>
                  onChange({ ...action, to: `address:${e.target.value}` })
                }
                className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
              />
            </FieldRow>
          )}
          <FieldRow label="Subject">
            <TriggerTemplateField
              mode="plain"
              variables={variables}
              value={action.subject}
              onChange={(v) => onChange({ ...action, subject: v })}
              placeholder="Subject — supports #{variables}"
            />
          </FieldRow>
          <FieldRow label="Body">
            <TriggerTemplateField
              mode="html"
              variables={variables}
              value={action.body_html}
              onChange={(v) => onChange({ ...action, body_html: v })}
              placeholder="Mail body — supports #{variables}"
            />
          </FieldRow>
        </div>
      );
    case "send_survey":
      return <SendSurveyFields action={action} onChange={onChange} />;
    case "notify_agent":
      return <NotifyAgentFields action={action} onChange={onChange} variables={variables} />;
    case "handle_backoffice_form":
      return <HandleBackofficeFormFields action={action} onChange={onChange} variables={variables} />;
    case "create_linked_ticket":
      return (
        <CreateLinkedTicketFields
          action={action}
          onChange={onChange}
          taxonomies={taxonomies}
          variables={variables}
        />
      );
    case "prompt_confirm":
      return (
        <PromptConfirmFields
          action={action}
          onChange={onChange}
          taxonomies={taxonomies}
          variables={variables}
        />
      );
    case "require_contact_company":
      return (
        <RequireContactCompanyFields
          action={action}
          onChange={onChange}
          taxonomies={taxonomies}
          variables={variables}
        />
      );
    case "title_review":
      return <TitleReviewFields action={action} onChange={onChange} />;
  }
}

