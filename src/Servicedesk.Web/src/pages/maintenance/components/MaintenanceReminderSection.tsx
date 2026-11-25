import * as React from "react";
import { Input } from "@/components/ui/input";
import { RichTextEditor } from "@/components/RichTextEditor";
import { TokenVariableMenu } from "@/components/TokenVariableMenu";
import type { MaintenancePlanInput, TokenInfo } from "@/lib/maintenance-api";

type EditorHandle = { chain: () => { focus: () => { insertContent: (c: string) => { run: () => void } } } };
type ReminderFields = Pick<MaintenancePlanInput, "autoSend" | "reminderEnabled" | "reminderDays" | "reminderSubject" | "reminderBodyHtml">;

const FIELD = "space-y-1.5";
const LABEL = "text-xs font-medium text-muted-foreground";

/// v0.1.67 — the plan's reminder (owner decisions: once per round, only with
/// automatic sending, its own message). N days after the round's first mail
/// it goes to the customers who neither replied by mail nor answered through
/// the link — never after the intervention moment.
export function MaintenanceReminderSection({
  form, tokens, onChange,
}: {
  form: ReminderFields;
  tokens: TokenInfo[];
  onChange: (patch: Partial<ReminderFields>) => void;
}) {
  const editorRef = React.useRef<EditorHandle | null>(null);
  const on = form.autoSend && form.reminderEnabled;

  return (
    <div className="space-y-3">
      <label className="flex items-start gap-2 text-sm">
        <input type="checkbox" className="mt-1" checked={on} disabled={!form.autoSend}
          onChange={(e) => onChange({ reminderEnabled: e.target.checked })} />
        <span>
          Send a reminder to customers who did not answer
          <span className="block text-[11px] text-muted-foreground">
            {form.autoSend
              ? "Once per round, to customers without a mail reply and without an answer through the link. Not sent after the intervention."
              : "Needs \"Send automatically on the mail day\"."}
          </span>
        </span>
      </label>
      {on && (
        <>
          <div className="flex items-center gap-2 text-sm">
            <span>Send it</span>
            <Input type="number" min={1} max={60} aria-label="Days after the first mail" className="h-8 w-20" value={form.reminderDays}
              onChange={(e) => onChange({ reminderDays: Math.min(60, Math.max(1, Number(e.target.value) || 1)) })} />
            <span>days after the first mail (up to 15 minutes later).</span>
          </div>
          <div className={FIELD}>
            <label className={LABEL} htmlFor="sd-mp-rsubject">Reminder subject</label>
            <Input id="sd-mp-rsubject" value={form.reminderSubject} maxLength={500}
              onChange={(e) => onChange({ reminderSubject: e.target.value })} placeholder="Reminder: {{plan.name}} on {{campaign.proposedDate}}" />
          </div>
          <div className={FIELD}>
            <div className="flex items-center justify-between">
              <span className={LABEL}>Reminder message</span>
              <TokenVariableMenu tokens={tokens} onPick={(t) => editorRef.current?.chain().focus().insertContent(t).run()} />
            </div>
            <RichTextEditor content={form.reminderBodyHtml} onChange={(html) => onChange({ reminderBodyHtml: html })}
              minHeight="120px" maxHeight="280px"
              placeholder="Dear {{contact.firstName}}, we have not heard from you yet about {{campaign.proposedDateTime}}. {{campaign.approvalLink}}"
              onEditorReady={(e) => { editorRef.current = e as unknown as EditorHandle | null; }} />
          </div>
        </>
      )}
    </div>
  );
}
