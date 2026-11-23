import { useQuery } from "@tanstack/react-query";
import { Trash2, Check } from "lucide-react";
import { type NotifyAgentAction, type NotifyAgentTarget, type TriggerAction } from "../types";
import { type TriggerTemplateVariable } from "@/lib/triggers-api";
import { surveysAgentApi } from "@/lib/surveys-api";
import { formTemplatesApi } from "@/lib/forms-api";
import { userApi } from "@/lib/ticket-api";
import { TriggerTemplateField } from "../TriggerTemplateField";
import { AgentPicker } from "@/components/AgentPicker";
import { cn } from "@/lib/utils";
import { FieldRow, NativeSelect } from "./actionFieldPrimitives";

/// Form fragment for the title_review action — the single gate action on a
/// gate:first_open trigger. Configures the blocking dialog the agent sees
/// on first open: heading, an optional question line, the label above the
/// editable subject field, and the approve button. The approval note is a
/// separate add_internal_note action, so there's no note template here.
export function TitleReviewFields({
  action,
  onChange,
}: {
  action: Extract<TriggerAction, { kind: "title_review" }>;
  onChange: (next: TriggerAction) => void;
}) {
  return (
    <div className="space-y-3">
      <FieldRow label="Dialog title">
        <input
          type="text"
          value={action.title}
          onChange={(e) => onChange({ ...action, title: e.target.value })}
          placeholder="Review the ticket title"
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
          Show a question above the editable title field
        </label>
        {action.show_message && (
          <textarea
            value={action.message}
            onChange={(e) => onChange({ ...action, message: e.target.value })}
            rows={2}
            placeholder="Is this title suitable? Adjust it if needed."
            className="mt-2 w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          />
        )}
      </FieldRow>

      <FieldRow label="Original request">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={action.show_request}
            onChange={(e) => onChange({ ...action, show_request: e.target.checked })}
            className="h-4 w-4 rounded border-glass-strong bg-glass"
          />
          Show the ticket's original request above the title field
        </label>
      </FieldRow>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <FieldRow label="Title field label">
          <input
            type="text"
            value={action.field_label}
            onChange={(e) => onChange({ ...action, field_label: e.target.value })}
            placeholder="Ticket title"
            className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          />
        </FieldRow>
        <FieldRow label="Approve button label">
          <input
            type="text"
            value={action.confirm_label}
            onChange={(e) => onChange({ ...action, confirm_label: e.target.value })}
            placeholder="This title is suitable"
            className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
          />
        </FieldRow>
      </div>

      <p className="rounded-md border border-glass-strong bg-glass px-3 py-2 text-[11px] text-muted-foreground/80">
        Shown the first time an agent opens a matching ticket (once per ticket). The agent reviews or edits the title and clicks the approve button before they can work the ticket. Add an <span className="text-foreground/80">internal note</span> action below to record the approval — it can use <code className="font-mono">#{"{agent.name}"}</code> and <code className="font-mono">#{"{ticket.subject}"}</code>.
      </p>
    </div>
  );
}


/// v0.1.54 — notify_agent: bell notification + live toast for agents.
export function NotifyAgentFields({
  action,
  onChange,
  variables,
}: {
  action: NotifyAgentAction;
  onChange: (next: TriggerAction) => void;
  variables: TriggerTemplateVariable[];
}) {
  const toggle = (t: NotifyAgentTarget, on: boolean) =>
    onChange({ ...action, targets: on ? [...action.targets.filter((x) => x !== t), t] : action.targets.filter((x) => x !== t) });
  const agentsQ = useQuery({ queryKey: ["agents"], queryFn: userApi.listAgents, staleTime: 5 * 60_000 });
  const agentName = (id: string) => {
    const a = agentsQ.data?.find((x) => x.id === id);
    return a ? a.email : "Unknown agent";
  };
  return (
    <div className="space-y-3">
      <FieldRow label="Recipients">
        <div className="flex flex-wrap gap-x-5 gap-y-2 text-sm">
          <label className="inline-flex items-center gap-2">
            <input type="checkbox" checked={action.targets.includes("form_submitter")}
              onChange={(e) => toggle("form_submitter", e.target.checked)} />
            Agent who filled in the form
          </label>
          <label className="inline-flex items-center gap-2">
            <input type="checkbox" checked={action.targets.includes("ticket_owner")}
              onChange={(e) => toggle("ticket_owner", e.target.checked)} />
            Ticket owner
          </label>
        </div>
      </FieldRow>
      <FieldRow label="Also notify">
        <div className="space-y-2">
          {action.user_ids.length > 0 && (
            <div className="flex flex-wrap gap-1.5">
              {action.user_ids.map((id) => (
                <span key={id} className="inline-flex items-center gap-1 rounded-md border border-glass-strong bg-glass px-2 py-0.5 text-xs">
                  {agentName(id)}
                  <button type="button" aria-label={`Remove ${agentName(id)}`}
                    onClick={() => onChange({ ...action, user_ids: action.user_ids.filter((x) => x !== id) })}
                    className="text-muted-foreground hover:text-foreground">
                    <Trash2 className="h-3 w-3" />
                  </button>
                </span>
              ))}
            </div>
          )}
          <AgentPicker
            value={null}
            placeholder="Add an agent…"
            onChange={(id) => {
              if (id && !action.user_ids.includes(id)) onChange({ ...action, user_ids: [...action.user_ids, id] });
            }}
          />
        </div>
      </FieldRow>
      <FieldRow label="Message">
        <TriggerTemplateField
          mode="plain"
          variables={variables}
          value={action.message}
          onChange={(v) => onChange({ ...action, message: v })}
          placeholder="e.g. #{form.name} handled by #{form.handled_by}: #{form.handled_note}"
        />
      </FieldRow>
      <p className="text-[11px] text-muted-foreground/80">
        Shows in the bell and as a toast. Only active agents receive it, and never the person whose own action started the trigger.
      </p>
    </div>
  );
}


export function HandleBackofficeFormFields({
  action,
  onChange,
  variables,
}: {
  action: Extract<TriggerAction, { kind: "handle_backoffice_form" }>;
  onChange: (next: TriggerAction) => void;
  variables: TriggerTemplateVariable[];
}) {
  const templatesQ = useQuery({
    queryKey: ["form-templates", "admin-list"],
    queryFn: () => formTemplatesApi.list(),
    staleTime: 60_000,
  });
  const templates = (templatesQ.data ?? []).filter((t) => t.sendToBackoffice);
  return (
    <div className="space-y-3">
      <FieldRow label="Form templates">
        <div className="flex flex-wrap gap-2">
          {templates.map((t) => {
            const on = action.template_ids.includes(t.id);
            return (
              <button key={t.id} type="button" aria-pressed={on}
                onClick={() => onChange({
                  ...action,
                  template_ids: on ? action.template_ids.filter((x) => x !== t.id) : [...action.template_ids, t.id],
                })}
                className={cn("inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition-colors",
                  on ? "border-primary/40 bg-primary/12 text-foreground" : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover")}>
                {on && <Check className="h-3.5 w-3.5" />}
                {t.name}{!t.isActive ? " (inactive)" : ""}
              </button>
            );
          })}
          {templatesQ.isSuccess && templates.length === 0 && (
            <span className="text-[11px] text-muted-foreground/70">No form templates are sent to the back office.</span>
          )}
        </div>
      </FieldRow>
      <FieldRow label="Note (optional)">
        <TriggerTemplateField
          mode="plain"
          variables={variables}
          value={action.note}
          onChange={(v) => onChange({ ...action, note: v })}
          placeholder="Handling note — supports #{variables}"
        />
      </FieldRow>
      <p className="text-[11px] text-muted-foreground/80">
        Marks every open back-office form of these templates on the ticket as handled. Use it on the planning form's save trigger so the intervention request is handled wherever the planning form was filled in. A form handled this way does not start a 'handled' trigger pass.
      </p>
    </div>
  );
}


export function SendSurveyFields({
  action,
  onChange,
}: {
  action: Extract<TriggerAction, { kind: "send_survey" }>;
  onChange: (next: TriggerAction) => void;
}) {
  const surveysQ = useQuery({
    queryKey: ["surveys", "usable"],
    queryFn: () => surveysAgentApi.usable(),
    staleTime: 60_000,
  });
  const ttlText = action.ttl_days_override?.toString() ?? "";
  return (
    <div className="space-y-3">
      <FieldRow label="Survey">
        <NativeSelect
          value={action.survey_id}
          onChange={(v) => onChange({ ...action, survey_id: v })}
        >
          <option value="">— pick a survey —</option>
          {(surveysQ.data ?? []).map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </NativeSelect>
      </FieldRow>
      <FieldRow label="TTL override (days, optional)">
        <input
          type="number"
          value={ttlText}
          min={1}
          max={365}
          onChange={(e) => {
            const raw = e.target.value;
            const next = raw === "" ? null : Math.max(1, Math.min(365, Number(raw)));
            onChange({ ...action, ttl_days_override: next });
          }}
          placeholder="Survey-level default"
          className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
        />
      </FieldRow>
      <FieldRow label="Recipient override (optional)">
        <input
          type="email"
          value={action.recipient_override ?? ""}
          onChange={(e) =>
            onChange({
              ...action,
              recipient_override: e.target.value || null,
            })
          }
          placeholder="Defaults to the ticket requester's email"
          className="w-full rounded-md border border-glass bg-glass px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring"
        />
      </FieldRow>
      <p className="text-[11px] text-muted-foreground/80">
        Idempotent: dispatch is skipped while an active invitation already
        exists for this (survey, ticket) pair.
      </p>
    </div>
  );
}

