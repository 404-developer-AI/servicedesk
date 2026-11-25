import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { toast } from "sonner";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { RichTextEditor } from "@/components/RichTextEditor";
import { TokenVariableMenu } from "@/components/TokenVariableMenu";
import { formatDateMedium } from "@/lib/dateFormat";
import { errorMessage } from "@/lib/http";
import { taxonomyApi } from "@/lib/taxonomy-api";
import { ticketTemplatesApi } from "@/lib/ticketTemplates-api";
import {
  MAINTENANCE_KEY, mailDateOf, maintenanceApi, maintenanceKeys, type MaintenancePlanInput,
} from "@/lib/maintenance-api";
import { minuteToTime, timeToMinute } from "@/lib/timeOfDay";
import { MaintenanceMembersEditor } from "./MaintenanceMembersEditor";
import { MaintenanceReminderSection } from "./MaintenanceReminderSection";

type EditorHandle = { chain: () => { focus: () => { insertContent: (c: string) => { run: () => void } } } };

const FIELD = "space-y-1.5";
const LABEL = "text-xs font-medium text-muted-foreground";
const SECTION = "space-y-3 rounded-md border border-glass p-3";
const SELECT = "h-9 w-full rounded-md border border-glass bg-glass px-2 text-sm text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring [&_option]:bg-popover [&_option]:text-popover-foreground";

/// v0.1.67 — create or edit a maintenance plan: the intervention (date,
/// time, manual or automatic schedule), the customers it applies to, the
/// ticket each of them gets and the mail. With "Send automatically" the
/// round's mail leaves on the mail day as the user who saved the plan;
/// otherwise the round's campaign waits as a draft.
export function MaintenancePlanDialog({
  open, onOpenChange, planId,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  planId: string | null;
}) {
  const qc = useQueryClient();
  const detail = useQuery({ queryKey: maintenanceKeys.plan(planId ?? ""), queryFn: () => maintenanceApi.plan(planId!), enabled: open && !!planId });
  const queues = useQuery({ queryKey: ["queues"], queryFn: taxonomyApi.queues.list, enabled: open, staleTime: 300_000 });
  const statuses = useQuery({ queryKey: ["statuses"], queryFn: taxonomyApi.statuses.list, enabled: open, staleTime: 300_000 });
  const templates = useQuery({ queryKey: ["ticket-templates", "usable"], queryFn: ticketTemplatesApi.usable, enabled: open, staleTime: 300_000 });
  const tokens = useQuery({ queryKey: maintenanceKeys.tokens(), queryFn: maintenanceApi.tokens, enabled: open, staleTime: Infinity });
  const [form, setForm] = React.useState<MaintenancePlanInput>(empty);
  const [time, setTime] = React.useState("09:00");
  const [mailTime, setMailTime] = React.useState("09:00");
  const editorRef = React.useRef<EditorHandle | null>(null);

  React.useEffect(() => {
    if (!open) return;
    if (!planId) { setForm(empty()); setTime("09:00"); setMailTime("09:00"); return; }
    if (!detail.data) return;
    const { plan, members } = detail.data;
    setForm({
      name: plan.name, notes: plan.notes, queueId: plan.queueId, ticketTemplateId: plan.ticketTemplateId,
      ticketSubject: plan.ticketSubject, mailSubject: plan.mailSubject, mailBodyHtml: plan.mailBodyHtml,
      scheduleMode: plan.scheduleMode, intervalMonths: plan.intervalMonths, nextInterventionDate: plan.nextInterventionDate,
      interventionMinute: plan.interventionMinute, mailDaysBefore: plan.mailDaysBefore, mailMinute: plan.mailMinute, autoSend: plan.autoSend,
      pendingStatusId: plan.pendingStatusId, pendingDays: plan.pendingDays, isActive: plan.isActive, members,
      otherTimeNotice: plan.otherTimeNotice,
      reminderEnabled: plan.reminderEnabled, reminderDays: plan.reminderDays, reminderSubject: plan.reminderSubject,
      reminderBodyHtml: plan.reminderBodyHtml,
    });
    setTime(minuteToTime(plan.interventionMinute));
    setMailTime(minuteToTime(plan.mailMinute));
  }, [open, planId, detail.data]);
  const set = <K extends keyof MaintenancePlanInput>(k: K, v: MaintenancePlanInput[K]) => setForm((f) => ({ ...f, [k]: v }));

  const save = useMutation({
    mutationFn: async () => {
      const input = {
        ...form,
        interventionMinute: timeToMinute(time) ?? form.interventionMinute,
        mailMinute: timeToMinute(mailTime) ?? form.mailMinute,
        // A reminder only exists with automatic sending (server-enforced).
        reminderEnabled: form.autoSend && form.reminderEnabled,
      };
      if (planId) await maintenanceApi.updatePlan(planId, input); else await maintenanceApi.createPlan(input);
    },
    onSuccess: () => { void qc.invalidateQueries({ queryKey: MAINTENANCE_KEY }); toast.success(planId ? "Plan saved" : "Plan created"); onOpenChange(false); },
    onError: (err) => toast.error(errorMessage(err, "Could not save the plan")),
  });
  const remove = useMutation({
    mutationFn: () => maintenanceApi.deletePlan(planId!),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: MAINTENANCE_KEY }); toast.success("Plan deleted"); onOpenChange(false); },
    onError: (err) => toast.error(errorMessage(err, "Could not delete the plan")),
  });

  const automatic = form.scheduleMode === "Automatic";
  const valid = !!form.name.trim() && !!form.queueId && timeToMinute(time) !== null && timeToMinute(mailTime) !== null
    && (!automatic || (!!form.intervalMonths && !!form.nextInterventionDate))
    && form.members.every((m) => !!m.contactId)
    && (!(form.autoSend && form.reminderEnabled) || (!!form.reminderSubject.trim() && !!form.reminderBodyHtml.trim()));
  const pendingStatuses = (statuses.data ?? []).filter((s) => s.isActive && s.stateCategory === "Pending");
  const hasHistory = !!detail.data?.plan.lastRound;

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!save.isPending) onOpenChange(o); }}>
      <DialogContent className="flex max-h-[90vh] w-[calc(100vw-2rem)] max-w-4xl flex-col">
        <DialogHeader>
          <DialogTitle>{planId ? "Edit maintenance plan" : "New maintenance plan"}</DialogTitle>
          <DialogDescription>
            A recurring intervention for several customers. On the mail day every customer gets a new ticket and the mail.
          </DialogDescription>
        </DialogHeader>
        <form id="sd-maintenance-plan" className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1"
          onSubmit={(e) => { e.preventDefault(); if (valid) save.mutate(); }}>
          <div className="grid gap-4 sm:grid-cols-2">
            <div className={FIELD}>
              <label className={LABEL} htmlFor="sd-mp-name">Name</label>
              <Input id="sd-mp-name" value={form.name} maxLength={200} onChange={(e) => set("name", e.target.value)} placeholder="e.g. Firewall updates" />
            </div>
            <div className={FIELD}>
              <label className={LABEL} htmlFor="sd-mp-queue">Queue for the tickets</label>
              <select id="sd-mp-queue" className={SELECT} value={form.queueId} onChange={(e) => set("queueId", e.target.value)}>
                <option value="">Pick a queue…</option>
                {(queues.data ?? []).filter((q) => q.isActive).map((q) => <option key={q.id} value={q.id}>{q.name}</option>)}
              </select>
            </div>
          </div>

          <div className={SECTION}>
            <div className="text-sm font-medium">Intervention</div>
            <div className="grid gap-3 sm:grid-cols-3">
              <div className={FIELD}>
                <label className={LABEL} htmlFor="sd-mp-date">Planned intervention</label>
                <Input id="sd-mp-date" type="date" value={form.nextInterventionDate ?? ""}
                  onChange={(e) => set("nextInterventionDate", e.target.value || null)} />
              </div>
              <div className={FIELD}>
                <label className={LABEL} htmlFor="sd-mp-time">Time</label>
                <Input id="sd-mp-time" type="time" value={time} onChange={(e) => setTime(e.target.value)} />
              </div>
              <div className={FIELD}>
                <label className={LABEL} htmlFor="sd-mp-mode">Next intervention</label>
                <select id="sd-mp-mode" className={SELECT} value={form.scheduleMode}
                  onChange={(e) => set("scheduleMode", e.target.value as MaintenancePlanInput["scheduleMode"])}>
                  <option value="Manual">Manual — I set it myself</option>
                  <option value="Automatic">Automatic — repeat</option>
                </select>
              </div>
              <div className={FIELD}>
                <label className={LABEL} htmlFor="sd-mp-days">Mail (days before)</label>
                <Input id="sd-mp-days" type="number" min={0} max={180} value={form.mailDaysBefore}
                  onChange={(e) => set("mailDaysBefore", Math.min(180, Math.max(0, Number(e.target.value) || 0)))} />
              </div>
              <div className={FIELD}>
                <label className={LABEL} htmlFor="sd-mp-mailtime">Mail time</label>
                <Input id="sd-mp-mailtime" type="time" value={mailTime} onChange={(e) => setMailTime(e.target.value)} />
              </div>
              <div className={FIELD}>
                <span className={LABEL}>Tickets and mail on</span>
                <div className="flex h-9 items-center text-sm font-medium text-foreground">
                  {form.nextInterventionDate
                    ? `${formatDateMedium(mailDateOf(form.nextInterventionDate, form.mailDaysBefore))} · ${mailTime || "—"}`
                    : "—"}
                </div>
              </div>
            </div>
            {automatic && (
              <div className="flex items-center gap-2 text-sm">
                <span>Repeat every</span>
                <Input type="number" min={1} max={60} className="h-8 w-20" value={form.intervalMonths ?? ""}
                  onChange={(e) => set("intervalMonths", e.target.value ? Number(e.target.value) : null)} />
                <span>months, on the same day of the month.</span>
              </div>
            )}
            <p className="text-[11px] text-muted-foreground">
              {form.nextInterventionDate
                ? `${form.autoSend ? "The mails go out" : "The tickets and the draft campaign are made"} at that moment (server time) — the planner checks every 15 minutes, so allow up to 15 minutes' delay. `
                : "Without a date nothing is planned. "}
              {automatic ? "After each round the next date moves on by the interval." : "After a round, set the next date yourself."}
            </p>
          </div>

          <div className={SECTION}>
            <MaintenanceMembersEditor members={form.members} onChange={(m) => set("members", m)} />
          </div>

          <div className={SECTION}>
            <div className="text-sm font-medium">Ticket per customer</div>
            <div className="grid gap-3 sm:grid-cols-2">
              <div className={FIELD}>
                <label className={LABEL} htmlFor="sd-mp-template">Ticket template (optional)</label>
                <select id="sd-mp-template" className={SELECT} value={form.ticketTemplateId ?? ""}
                  onChange={(e) => set("ticketTemplateId", e.target.value || null)}>
                  <option value="">No template</option>
                  {(templates.data ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
                </select>
              </div>
              <div className={FIELD}>
                <div className="flex items-center justify-between">
                  <label className={LABEL} htmlFor="sd-mp-subject">Ticket subject (when the template has none)</label>
                  <TokenVariableMenu tokens={tokens.data?.ticket ?? []} onPick={(t) => set("ticketSubject", form.ticketSubject + t)} />
                </div>
                <Input id="sd-mp-subject" value={form.ticketSubject} maxLength={500} onChange={(e) => set("ticketSubject", e.target.value)}
                  placeholder="{{plan.name}} — {{member.label}}" />
              </div>
            </div>
          </div>

          <div className={SECTION}>
            <div className="text-sm font-medium">Mail</div>
            <div className={FIELD}>
              <label className={LABEL} htmlFor="sd-mp-msubject">Subject</label>
              <Input id="sd-mp-msubject" value={form.mailSubject} maxLength={500} onChange={(e) => set("mailSubject", e.target.value)}
                placeholder="Firewall update on {{campaign.proposedDate}}" />
            </div>
            <div className={FIELD}>
              <div className="flex items-center justify-between">
                <span className={LABEL}>Message</span>
                <TokenVariableMenu tokens={tokens.data?.mail ?? []} onPick={(t) => editorRef.current?.chain().focus().insertContent(t).run()} />
              </div>
              <RichTextEditor content={form.mailBodyHtml} onChange={(html) => set("mailBodyHtml", html)} minHeight="150px" maxHeight="320px"
                placeholder="Dear {{contact.firstName}}, we plan the intervention on {{campaign.proposedDateTime}}. {{campaign.approvalLink}}"
                onEditorReady={(e) => { editorRef.current = e as unknown as EditorHandle | null; }} />
            </div>
            <div className={FIELD}>
              <label className={LABEL} htmlFor="sd-mp-othertime">Note with "Another moment" (optional, shown on the answer page)</label>
              <textarea id="sd-mp-othertime" rows={2} maxLength={1000} value={form.otherTimeNotice}
                onChange={(e) => set("otherTimeNotice", e.target.value)}
                placeholder="e.g. An intervention at another moment is charged at 150 % of the normal rate."
                className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-hidden focus:ring-1 focus:ring-ring" />
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="flex items-start gap-2 text-sm">
                <input type="checkbox" className="mt-1" checked={form.autoSend} onChange={(e) => set("autoSend", e.target.checked)} />
                <span>
                  Send automatically on the mail day
                  <span className="block text-[11px] text-muted-foreground">
                    Mails go out from the queue's mailbox. {form.autoSend ? "You (the user who saves the plan) are recorded as the sender on the ticket and in the signature. " : ""}Off: the round's campaign waits as a draft for you to send.
                  </span>
                </span>
              </label>
              <div className={FIELD}>
                <label className={LABEL} htmlFor="sd-mp-pending">After the mail, set the ticket to</label>
                <select id="sd-mp-pending" className={SELECT} value={form.pendingStatusId ?? ""} onChange={(e) => set("pendingStatusId", e.target.value || null)}>
                  <option value="">Leave the status as it is</option>
                  {pendingStatuses.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
                </select>
                {form.pendingStatusId && (
                  <div className="flex items-center gap-2 pt-1 text-sm">
                    <select aria-label="How long the ticket waits" className={SELECT + " w-auto"} value={form.pendingDays === null ? "until" : "days"}
                      onChange={(e) => set("pendingDays", e.target.value === "until" ? null : 2)}>
                      <option value="until">until the intervention</option>
                      <option value="days">for a number of days</option>
                    </select>
                    {form.pendingDays !== null && (
                      <>
                        <Input type="number" min={1} max={365} aria-label="Days" className="h-9 w-20" value={form.pendingDays}
                          onChange={(e) => set("pendingDays", Math.min(365, Math.max(1, Number(e.target.value) || 1)))} />
                        <span className="text-muted-foreground">days after the mail</span>
                      </>
                    )}
                  </div>
                )}
                <p className="text-[11px] text-muted-foreground">
                  Applied when the mail is sent automatically; never past the intervention. When you send the campaign yourself, you choose it in the send dialog.
                </p>
              </div>
            </div>
          </div>

          <div className={SECTION}>
            <div className="text-sm font-medium">Reminder</div>
            <MaintenanceReminderSection form={form} tokens={tokens.data?.mail ?? []} onChange={(patch) => setForm((f) => ({ ...f, ...patch }))} />
          </div>

          <div className={FIELD}>
            <label className={LABEL} htmlFor="sd-mp-notes">Notes</label>
            <textarea id="sd-mp-notes" rows={2} maxLength={4000} value={form.notes} onChange={(e) => set("notes", e.target.value)}
              className="w-full rounded-md border border-glass bg-glass px-3 py-2 text-sm text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring" />
          </div>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={form.isActive} onChange={(e) => set("isActive", e.target.checked)} /> Active
          </label>
        </form>
        <DialogFooter className="sm:justify-between">
          {planId && !hasHistory ? (
            <Button variant="ghost" className="text-destructive" disabled={remove.isPending || save.isPending} onClick={() => remove.mutate()}>Delete</Button>
          ) : <span className="text-[11px] text-muted-foreground">{planId ? "A plan that already ran is switched off instead of deleted." : ""}</span>}
          <div className="flex gap-2">
            <Button variant="ghost" onClick={() => onOpenChange(false)} disabled={save.isPending}>Cancel</Button>
            <Button type="submit" form="sd-maintenance-plan" disabled={!valid || save.isPending}>
              {save.isPending && <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />}
              {planId ? "Save" : "Create plan"}
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function empty(): MaintenancePlanInput {
  return {
    name: "", notes: "", queueId: "", ticketTemplateId: null, ticketSubject: "", mailSubject: "", mailBodyHtml: "",
    scheduleMode: "Manual", intervalMonths: null, nextInterventionDate: null, interventionMinute: 540, mailDaysBefore: 14, mailMinute: 540,
    autoSend: false, pendingStatusId: null, pendingDays: null, isActive: true, members: [],
    reminderEnabled: false, reminderDays: 3, reminderSubject: "", reminderBodyHtml: "", otherTimeNotice: "",
  };
}
