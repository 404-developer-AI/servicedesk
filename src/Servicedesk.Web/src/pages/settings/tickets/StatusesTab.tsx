import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  taxonomyApi,
  STATE_CATEGORIES,
  type Status,
  type StatusInput,
  type StatusStateCategory,
} from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";
import { useConfirm } from "@/components/ConfirmDialog";
import { TableShell, ColorSwatch, useDeleteHandler, Field, LoadingSkeleton } from "@/pages/settings/tickets/shared";

const STATE_CATEGORY_COLOR: Record<StatusStateCategory, string> = {
  New: "text-sky-200 border-sky-400/20 bg-sky-400/10",
  Open: "text-indigo-200 border-indigo-400/20 bg-indigo-400/10",
  Pending: "text-amber-200 border-amber-400/20 bg-amber-400/10",
  Resolved: "text-emerald-200 border-emerald-400/20 bg-emerald-400/10",
  Closed: "text-slate-300 border-slate-400/20 bg-slate-400/10",
};

export function StatusesTab() {
  const confirm = useConfirm();
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Status | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.statuses.remove(id),
    ["taxonomy", "statuses"],
    "Status",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New status</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Name</th>
              <th className="px-4 py-3 font-medium">Semantic</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Default</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((s) => (
              <tr key={s.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">
                  <div className="flex items-center gap-2">
                    {s.name}
                    {s.isSystem && (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        system
                      </Badge>
                    )}
                  </div>
                </td>
                <td className="px-4 py-3">
                  <Badge
                    className={cn(
                      "border text-[10px] font-normal",
                      STATE_CATEGORY_COLOR[s.stateCategory],
                    )}
                  >
                    {s.stateCategory}
                  </Badge>
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={s.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{s.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-xs text-muted-foreground">
                  {s.isDefault ? "yes" : "—"}
                </td>
                <td className="px-4 py-3 text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(s)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={s.isSystem || del.isPending}
                    onClick={async () => {
                      if (
                        await confirm({
                          title: "Delete status",
                          description: `Delete status "${s.name}"?`,
                          confirmLabel: "Delete",
                          tone: "destructive",
                        })
                      )
                        del.mutate(s.id);
                    }}
                  >
                    Delete
                  </Button>
                </td>
              </tr>
            ))}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={6} className="p-8 text-center text-sm text-muted-foreground">
                  No statuses yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <StatusDialog
          status={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "statuses"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function StatusDialog({
  status,
  onClose,
  onSaved,
}: {
  status: Status | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<StatusInput>(() => ({
    name: status?.name ?? "",
    stateCategory: status?.stateCategory ?? "Open",
    color: status?.color ?? "#7c7cff",
    icon: status?.icon ?? "circle",
    sortOrder: status?.sortOrder ?? 0,
    isActive: status?.isActive ?? true,
    isDefault: status?.isDefault ?? false,
  }));

  const save = useMutation({
    mutationFn: async () =>
      status
        ? taxonomyApi.statuses.update(status.id, form)
        : taxonomyApi.statuses.create(form),
    onSuccess: () => {
      toast.success(status ? "Status updated" : "Status created");
      onSaved();
    },
    onError: () => toast.error("Save failed"),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{status ? "Edit status" : "New status"}</DialogTitle>
          <DialogDescription>
            Pin each status to a semantic category so SLA and reporting stay correct even
            when you rename the display label.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <Input
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </Field>
          <Field label="Semantic category">
            <select
              value={form.stateCategory}
              onChange={(e) =>
                setForm((f) => ({ ...f, stateCategory: e.target.value as StatusStateCategory }))
              }
              className="h-9 w-full rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-hidden focus:border-primary/60"
            >
              {STATE_CATEGORIES.map((c) => (
                <option key={c} value={c} className="bg-background">
                  {c}
                </option>
              ))}
            </select>
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Sort order">
              <Input
                type="number"
                value={form.sortOrder}
                onChange={(e) => setForm((f) => ({ ...f, sortOrder: Number(e.target.value) }))}
              />
            </Field>
            <Field label="Color">
              <input
                type="color"
                value={form.color ?? "#7c7cff"}
                onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                className="h-9 w-full cursor-pointer rounded-md border border-glass bg-transparent"
              />
            </Field>
          </div>
          <div className="flex flex-col gap-2 text-xs text-muted-foreground">
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
              />
              Active
            </label>
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={form.isDefault}
                onChange={(e) => setForm((f) => ({ ...f, isDefault: e.target.checked }))}
              />
              Default for new tickets (only one status can be the default)
            </label>
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !form.name}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
