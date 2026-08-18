import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Copy, ListChecks, Lock, Pencil, Plus, Trash2 } from "lucide-react";
import { settingsApi, taxonomyApi } from "@/lib/api";
import { checklistTemplateApi, checklistErrorMessage, type ChecklistTemplateSummary } from "@/lib/checklist-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { ChecklistTemplateEditorSheet } from "./ChecklistTemplateEditorSheet";

/// Settings → Tickets → Checklists: the global knobs on top, the template
/// catalogue below. Templates are attached to tickets as snapshots, so
/// editing or deleting one here never touches a checklist already on a
/// ticket.
export function ChecklistsSettingsTab({ initialTemplateId }: { initialTemplateId?: string | null }) {
  const qc = useQueryClient();
  const [editing, setEditing] = React.useState<string | "new" | null>(initialTemplateId ?? null);

  const settingsQ = useQuery({
    queryKey: ["settings", "tickets-checklists"],
    queryFn: () => settingsApi.list("Tickets"),
    staleTime: 60_000,
  });
  const entries = settingsQ.data;
  const val = (key: string, fallback: string) => entries?.find((e) => e.key === key)?.value ?? fallback;
  const enabled = val("Checklists.Enabled", "true") === "true";
  const logItems = val("Checklists.LogItemChangesToTimeline", "false") === "true";
  const categories = React.useMemo(
    () =>
      new Set(
        val("Checklists.BlockingStateCategories", "Resolved,Closed")
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean),
      ),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [entries],
  );
  const savedMaxPerTicket = val("Checklists.MaxPerTicket", "10");
  const savedMaxItems = val("Checklists.MaxItemsPerChecklist", "300");
  const [maxPerTicket, setMaxPerTicket] = React.useState<string | null>(null);
  const [maxItems, setMaxItems] = React.useState<string | null>(null);

  const update = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => settingsApi.update(key, value),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "tickets-checklists"] });
      qc.invalidateQueries({ queryKey: ["settings", "checklists"] });
      setMaxPerTicket(null);
      setMaxItems(null);
      toast.success("Setting updated");
    },
    onError: () => toast.error("Could not update setting"),
  });

  const toggleCategory = (cat: "Resolved" | "Closed") => {
    const next = new Set(categories);
    if (next.has(cat)) next.delete(cat);
    else next.add(cat);
    update.mutate({ key: "Checklists.BlockingStateCategories", value: ["Resolved", "Closed"].filter((c) => next.has(c)).join(",") });
  };

  const templatesQ = useQuery({
    queryKey: ["checklist-templates"],
    queryFn: checklistTemplateApi.list,
  });
  const queuesQ = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });
  const queueName = (id: string) => queuesQ.data?.find((q) => q.id === id)?.name ?? "?";

  const duplicate = useMutation({
    mutationFn: (id: string) => checklistTemplateApi.duplicate(id),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["checklist-templates"] });
      toast.success(`Duplicated as “${created.name}” (inactive)`);
      setEditing(created.id);
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not duplicate the template")),
  });
  const remove = useMutation({
    mutationFn: (id: string) => checklistTemplateApi.remove(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["checklist-templates"] });
      toast.success("Template deleted");
    },
    onError: (err) => toast.error(checklistErrorMessage(err, "Could not delete the template")),
  });
  const [confirmDelete, setConfirmDelete] = React.useState<string | null>(null);

  const perTicketValue = maxPerTicket ?? savedMaxPerTicket;
  const perTicketNum = Number.parseInt(perTicketValue, 10);
  const perTicketValid = Number.isFinite(perTicketNum) && perTicketNum >= 1 && perTicketNum <= 50;
  const itemsValue = maxItems ?? savedMaxItems;
  const itemsNum = Number.parseInt(itemsValue, 10);
  const itemsValid = Number.isFinite(itemsNum) && itemsNum >= 1 && itemsNum <= 1000;

  return (
    <div className="space-y-6">
      <section className="glass-card p-5">
        <div className="space-y-1">
          <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">Checklists</h2>
          <p className="text-xs text-muted-foreground/70">
            Agents attach a checklist to a ticket and tick items off inside the ticket (docked panel or a
            separate window), with a per-item log of who did what and comments. A checklist that "blocks
            closing" stops the ticket from being resolved or closed until every required item is done or
            marked not applicable — there is no override; not-applicable with a reason is the escape hatch.
          </p>
        </div>
        <div className="mt-4 space-y-3">
          <ToggleRow
            label="Enable checklists"
            description="Shows the checklist bar, header chip and panel on tickets and lets agents attach templates. When off, attached checklists are kept but hidden and the close block is not enforced."
            checked={enabled}
            disabled={settingsQ.isLoading || update.isPending}
            onCheckedChange={(v) => update.mutate({ key: "Checklists.Enabled", value: v ? "true" : "false" })}
          />
          <div className="rounded-md border border-glass bg-glass px-3 py-2.5">
            <div className="text-sm font-medium text-foreground">Statuses that count as "closing"</div>
            <div className="text-xs text-muted-foreground">
              A status change into one of these categories is refused while a blocking checklist still has
              required open items. Applies to the single-ticket status change and to bulk actions alike.
            </div>
            <div className="mt-2 flex flex-wrap gap-2">
              {(["Resolved", "Closed"] as const).map((cat) => {
                const on = categories.has(cat);
                return (
                  <button
                    key={cat}
                    type="button"
                    disabled={update.isPending}
                    onClick={() => toggleCategory(cat)}
                    className={cn(
                      "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs transition-colors",
                      on
                        ? "border-amber-400/40 bg-amber-400/15 text-amber-100"
                        : "border-glass bg-glass text-muted-foreground hover:text-foreground",
                    )}
                  >
                    <Lock className="h-3 w-3" /> {cat}
                  </button>
                );
              })}
              {categories.size === 0 && (
                <span className="text-xs text-amber-300/80">No category selected — the close block is effectively off.</span>
              )}
            </div>
          </div>
          <ToggleRow
            label="Log item changes to the ticket timeline"
            description="Also write every tick / untick / not-applicable to the activity feed as a system event. Off by default: item changes always land in the checklist's own per-item log; only attach, remove, completed and reopened reach the timeline."
            checked={logItems}
            disabled={settingsQ.isLoading || update.isPending}
            onCheckedChange={(v) => update.mutate({ key: "Checklists.LogItemChangesToTimeline", value: v ? "true" : "false" })}
          />
          <div className="grid gap-3 sm:grid-cols-2">
            <NumberSetting
              label="Maximum checklists per ticket"
              value={perTicketValue}
              min={1}
              max={50}
              valid={perTicketValid}
              dirty={maxPerTicket !== null && maxPerTicket !== savedMaxPerTicket}
              disabled={settingsQ.isLoading || update.isPending}
              onChange={setMaxPerTicket}
              onSave={() => update.mutate({ key: "Checklists.MaxPerTicket", value: String(perTicketNum) })}
              hint="1–50."
            />
            <NumberSetting
              label="Maximum items per checklist"
              value={itemsValue}
              min={1}
              max={1000}
              valid={itemsValid}
              dirty={maxItems !== null && maxItems !== savedMaxItems}
              disabled={settingsQ.isLoading || update.isPending}
              onChange={setMaxItems}
              onSave={() => update.mutate({ key: "Checklists.MaxItemsPerChecklist", value: String(itemsNum) })}
              hint="Template items plus items added on the ticket. 1–1000."
            />
          </div>
        </div>
      </section>

      <section className="glass-card p-5">
        <div className="flex items-start justify-between gap-4">
          <div className="space-y-1">
            <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">Templates</h2>
            <p className="text-xs text-muted-foreground/70">
              What agents can attach. Queue scope decides where a template is offered (empty scope = every
              queue). Attached checklists are snapshots — later edits here don't change them. Tip: keep
              phases as separate templates (e.g. Onboarding · Technical intervention · Aftercare) and
              attach the next one when the ticket gets there.
            </p>
          </div>
          <Button type="button" size="sm" onClick={() => setEditing("new")}>
            <Plus className="h-4 w-4" /> New template
          </Button>
        </div>

        <div className="mt-4 overflow-hidden rounded-md border border-glass">
          {templatesQ.isLoading && (
            <div className="space-y-2 p-3">
              <Skeleton className="h-8 w-full" />
              <Skeleton className="h-8 w-full" />
            </div>
          )}
          {templatesQ.isError && <div className="p-3 text-sm text-destructive">Could not load the templates.</div>}
          {templatesQ.data && templatesQ.data.items.length === 0 && (
            <div className="flex flex-col items-center gap-2 p-8 text-center">
              <ListChecks className="h-6 w-6 text-muted-foreground/50" />
              <p className="text-sm text-muted-foreground">No templates yet.</p>
              <p className="text-xs text-muted-foreground/60">
                Create one and paste your steps — one line per item, lines starting with # start a section.
              </p>
            </div>
          )}
          {templatesQ.data && templatesQ.data.items.length > 0 && (
            <table className="w-full text-sm">
              <thead className="bg-glass text-left text-xs uppercase tracking-wide text-muted-foreground">
                <tr>
                  <th className="px-3 py-2 font-medium">Name</th>
                  <th className="px-3 py-2 font-medium">Queues</th>
                  <th className="px-3 py-2 font-medium text-right">Items</th>
                  <th className="px-3 py-2 font-medium">Blocks close</th>
                  <th className="px-3 py-2 font-medium">Active</th>
                  <th className="px-3 py-2 font-medium text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {templatesQ.data.items.map((t: ChecklistTemplateSummary) => (
                  <tr key={t.id} className={cn("border-t border-glass", !t.isActive && "opacity-60")}>
                    <td className="px-3 py-2">
                      <button type="button" onClick={() => setEditing(t.id)} className="text-left font-medium text-foreground hover:underline underline-offset-2">
                        {t.name}
                      </button>
                      {t.description && <div className="line-clamp-1 text-xs text-muted-foreground/70">{t.description}</div>}
                    </td>
                    <td className="px-3 py-2">
                      {t.queueIds.length === 0 ? (
                        <span className="text-xs text-muted-foreground/70">All queues</span>
                      ) : (
                        <span className="flex flex-wrap gap-1">
                          {t.queueIds.map((q) => (
                            <span key={q} className="rounded border border-glass bg-glass px-1.5 py-[1px] text-[11px] text-muted-foreground">
                              {queueName(q)}
                            </span>
                          ))}
                        </span>
                      )}
                    </td>
                    <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">{t.itemCount}</td>
                    <td className="px-3 py-2">
                      {t.blockClose ? (
                        <span className="inline-flex items-center gap-1 text-xs text-amber-200">
                          <Lock className="h-3 w-3" /> yes
                        </span>
                      ) : (
                        <span className="text-xs text-muted-foreground/60">no</span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <span className={cn("text-xs", t.isActive ? "text-emerald-300" : "text-muted-foreground/60")}>
                        {t.isActive ? "active" : "inactive"}
                      </span>
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-1">
                        <IconBtn title="Edit" onClick={() => setEditing(t.id)}>
                          <Pencil className="h-3.5 w-3.5" />
                        </IconBtn>
                        <IconBtn title="Duplicate" onClick={() => duplicate.mutate(t.id)} disabled={duplicate.isPending}>
                          <Copy className="h-3.5 w-3.5" />
                        </IconBtn>
                        {confirmDelete === t.id ? (
                          <>
                            <Button type="button" size="sm" variant="destructive" className="h-7 text-xs" disabled={remove.isPending} onClick={() => { remove.mutate(t.id); setConfirmDelete(null); }}>
                              Delete
                            </Button>
                            <Button type="button" size="sm" variant="ghost" className="h-7 text-xs" onClick={() => setConfirmDelete(null)}>
                              Keep
                            </Button>
                          </>
                        ) : (
                          <IconBtn title="Delete (attached checklists keep their snapshot)" onClick={() => setConfirmDelete(t.id)} danger>
                            <Trash2 className="h-3.5 w-3.5" />
                          </IconBtn>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </section>

      <ChecklistTemplateEditorSheet
        templateId={editing}
        maxItems={Number.parseInt(savedMaxItems, 10) || 300}
        onClose={() => setEditing(null)}
      />
    </div>
  );
}

function IconBtn({ title, onClick, disabled, danger, children }: { title: string; onClick: () => void; disabled?: boolean; danger?: boolean; children: React.ReactNode }) {
  return (
    <button
      type="button"
      title={title}
      aria-label={title}
      onClick={onClick}
      disabled={disabled}
      className={cn(
        "rounded-md p-1.5 text-muted-foreground/70 transition-colors hover:bg-glass-hover hover:text-foreground disabled:opacity-50",
        danger && "hover:text-red-300",
      )}
    >
      {children}
    </button>
  );
}

function ToggleRow({ label, description, checked, disabled, onCheckedChange }: { label: string; description: string; checked: boolean; disabled?: boolean; onCheckedChange: (v: boolean) => void }) {
  return (
    <label className="flex items-start justify-between gap-4 rounded-md border border-glass bg-glass px-3 py-2.5">
      <div className="min-w-0 space-y-0.5">
        <div className="text-sm font-medium text-foreground">{label}</div>
        <div className="text-xs text-muted-foreground">{description}</div>
      </div>
      <Switch checked={checked} disabled={disabled} onCheckedChange={onCheckedChange} />
    </label>
  );
}

function NumberSetting({ label, value, min, max, valid, dirty, disabled, onChange, onSave, hint }: { label: string; value: string; min: number; max: number; valid: boolean; dirty: boolean; disabled?: boolean; onChange: (v: string) => void; onSave: () => void; hint: string }) {
  return (
    <div className="rounded-md border border-glass bg-glass px-3 py-2.5">
      <label className="block space-y-1.5">
        <span className="text-sm font-medium text-foreground">{label}</span>
        <span className="flex items-center gap-2">
          <Input type="number" min={min} max={max} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} className="max-w-[140px]" />
          <Button type="button" size="sm" disabled={!dirty || !valid || disabled} onClick={onSave}>
            Save
          </Button>
        </span>
      </label>
      <p className={cn("mt-1 text-xs", valid ? "text-muted-foreground/70" : "text-destructive")}>{valid ? hint : `Enter a whole number between ${min} and ${max}.`}</p>
    </div>
  );
}
