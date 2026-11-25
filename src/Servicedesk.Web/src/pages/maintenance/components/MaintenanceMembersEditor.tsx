import * as React from "react";
import { Pencil, Plus, Search, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { CompanyPicker } from "@/components/CompanyPicker";
import { ContactPicker } from "@/components/ContactPicker";
import { cn } from "@/lib/utils";
import type { MaintenanceMember } from "@/lib/maintenance-api";
import { MemberNotesButton, MemberNotesRow } from "./MemberNotesButton";

/// v0.1.67 — the customers of a maintenance plan: company + contact (who
/// receives the mail) + an optional label for a second device at the same
/// customer ("HQ", "Branch"). Every active member gets its own ticket per
/// round; "Skip next round" leaves a member out of the next round only (the
/// server clears it when that round starts). Saved rows show as text (names
/// come with the plan, no lookup per row) until you edit them. A member that
/// already had a round is switched off on save instead of deleted (server).
const GRID = "grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_8rem_4rem_5rem_4.5rem] items-center gap-3";

export function MaintenanceMembersEditor({
  members,
  onChange,
}: {
  members: MaintenanceMember[];
  onChange: (members: MaintenanceMember[]) => void;
}) {
  const [editing, setEditing] = React.useState<Set<number>>(() => new Set());
  const [filter, setFilter] = React.useState("");
  const [notesFor, setNotesFor] = React.useState<string | null>(null);
  const update = (index: number, patch: Partial<MaintenanceMember>) =>
    onChange(members.map((m, i) => (i === index ? { ...m, ...patch } : m)));
  const add = () => {
    setEditing((s) => new Set(s).add(members.length));
    onChange([...members, { id: null, companyId: null, contactId: "", label: "", isActive: true, skipNextRound: false }]);
  };
  const remove = (index: number) => {
    setEditing((s) => new Set([...s].filter((i) => i !== index).map((i) => (i > index ? i - 1 : i))));
    onChange(members.filter((_, i) => i !== index));
  };

  const active = members.filter((m) => m.isActive).length;
  const skipping = members.filter((m) => m.isActive && m.skipNextRound).length;
  const q = filter.trim().toLowerCase();
  const rows = members
    .map((m, i) => ({ m, i }))
    .filter(({ m, i }) => !q || editing.has(i)
      || [m.companyName, m.contactName, m.contactEmail, m.label].some((s) => s?.toLowerCase().includes(q)));

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-sm font-medium">Customers</div>
          <div className="text-[11px] text-muted-foreground">
            {active} active{skipping > 0 ? ` · ${skipping} skip the next round` : ""} · each gets its own ticket and mail per round
          </div>
        </div>
        <div className="flex items-center gap-2">
          {members.length > 6 && (
            <div className="relative">
              <Search className="absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
              <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Find a customer…" className="h-8 w-48 pl-8 text-xs" />
            </div>
          )}
          <Button type="button" size="sm" variant="outline" onClick={add}>
            <Plus className="mr-1.5 h-3.5 w-3.5" /> Add customer
          </Button>
        </div>
      </div>

      {members.length === 0 ? (
        <p className="rounded-md border border-dashed border-glass px-3 py-6 text-center text-xs text-muted-foreground">
          No customers yet. Add the companies and contact persons this intervention applies to.
        </p>
      ) : (
        <div className="overflow-hidden rounded-md border border-glass">
          <div className={cn(GRID, "border-b border-glass bg-glass/60 px-3 py-2 text-[11px] font-medium text-muted-foreground")}>
            <span>Company</span>
            <span>Contact (receives the mail)</span>
            <span>Label</span>
            <span className="text-center">Active</span>
            <span className="text-center" title="Leave this customer out of the next round only, e.g. when the update was just done">Skip next</span>
            <span className="text-center">Notes</span>
          </div>
          <div className="max-h-[28rem] divide-y divide-glass overflow-y-auto">
            {rows.map(({ m, i }) => {
              const edit = editing.has(i) || !m.id;
              return (
                <React.Fragment key={m.id ?? `new-${i}`}>
                <div className={cn(GRID, "px-3 py-2 text-sm", !m.isActive && "opacity-60")}>
                  {edit ? (
                    <>
                      <CompanyPicker value={m.companyId} placeholder="Company…"
                        onChange={(id) => { if (id !== m.companyId) update(i, { companyId: id, companyName: null, contactId: "", contactName: null, contactEmail: null }); }} />
                      <ContactPicker value={m.contactId || null} companyId={m.companyId} placeholder={m.companyId ? "Contact of this company…" : "Contact…"}
                        onChange={(id, c) => update(i, {
                          contactId: id, contactName: null, contactEmail: null,
                          // Picked without a company: take the contact's own primary company.
                          ...(m.companyId ? {} : { companyId: c.primaryCompanyId, companyName: null }),
                        })} />
                    </>
                  ) : (
                    <>
                      <span className="truncate" title={m.companyName ?? undefined}>{m.companyName ?? <span className="text-muted-foreground">No company</span>}</span>
                      <button type="button" className="group flex min-w-0 items-center gap-1.5 text-left" title="Change company or contact"
                        onClick={() => setEditing((s) => new Set(s).add(i))}>
                        <span className="min-w-0">
                          <span className="block truncate">{m.contactName || m.contactEmail || "—"}</span>
                          {m.contactName && m.contactEmail && <span className="block truncate text-[11px] text-muted-foreground">{m.contactEmail}</span>}
                        </span>
                        <Pencil className="h-3 w-3 shrink-0 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100" />
                      </button>
                    </>
                  )}
                  <Input value={m.label} maxLength={100} placeholder="Optional" className="h-8" onChange={(e) => update(i, { label: e.target.value })} />
                  <div className="flex justify-center">
                    <Switch checked={m.isActive} aria-label="Active" onCheckedChange={(v) => update(i, { isActive: v })} />
                  </div>
                  <div className="flex justify-center">
                    <Switch checked={m.skipNextRound} disabled={!m.isActive} aria-label="Skip the next round"
                      onCheckedChange={(v) => update(i, { skipNextRound: v })} />
                  </div>
                  <div className="flex items-center justify-end gap-0.5">
                    <MemberNotesButton memberId={m.id} initialCount={m.noteCount ?? 0} open={notesFor === m.id}
                      onToggle={() => setNotesFor((cur) => (cur === m.id ? null : m.id))} />
                    <Button type="button" size="icon" variant="ghost" className="h-8 w-8 text-muted-foreground hover:text-destructive"
                      aria-label="Remove customer" onClick={() => remove(i)}>
                      <Trash2 className="h-3.5 w-3.5" />
                    </Button>
                  </div>
                </div>
                {m.id && notesFor === m.id && (
                  <MemberNotesRow memberId={m.id} title={[m.companyName ?? m.contactName, m.label].filter(Boolean).join(" — ")} />
                )}
                </React.Fragment>
              );
            })}
            {rows.length === 0 && <p className="px-3 py-4 text-center text-xs text-muted-foreground">No customer matches.</p>}
          </div>
        </div>
      )}
    </div>
  );
}
