import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { taxonomyApi, type Priority, type PriorityInput } from "@/lib/taxonomy-api";
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
import { useConfirm } from "@/components/ConfirmDialog";
import { TableShell, ColorSwatch, useDeleteHandler, Field, LoadingSkeleton } from "@/pages/settings/tickets/shared";

export function PrioritiesTab() {
  const confirm = useConfirm();
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Priority | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "priorities"],
    queryFn: () => taxonomyApi.priorities.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.priorities.remove(id),
    ["taxonomy", "priorities"],
    "Priority",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New priority</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Name</th>
              <th className="px-4 py-3 font-medium">Level</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Default</th>
              <th className="px-4 py-3 font-medium">Status</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((p) => (
              <tr key={p.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">{p.name}</td>
                <td className="px-4 py-3 text-muted-foreground">{p.level}</td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={p.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{p.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-xs text-muted-foreground">
                  {p.isDefault ? "yes" : "—"}
                </td>
                <td className="px-4 py-3">
                  {p.isActive ? (
                    <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                      active
                    </Badge>
                  ) : (
                    <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                      inactive
                    </Badge>
                  )}
                </td>
                <td className="px-4 py-3 text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(p)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={p.isDefault || del.isPending}
                    onClick={async () => {
                      if (
                        await confirm({
                          title: "Delete priority",
                          description: `Delete priority "${p.name}"?`,
                          confirmLabel: "Delete",
                          tone: "destructive",
                        })
                      )
                        del.mutate(p.id);
                    }}
                  >
                    Delete
                  </Button>
                </td>
              </tr>
            ))}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={7} className="p-8 text-center text-sm text-muted-foreground">
                  No priorities yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <PriorityDialog
          priority={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "priorities"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function PriorityDialog({
  priority,
  onClose,
  onSaved,
}: {
  priority: Priority | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<PriorityInput>(() => ({
    name: priority?.name ?? "",
    level: priority?.level ?? 20,
    color: priority?.color ?? "#7c7cff",
    icon: priority?.icon ?? "flag",
    sortOrder: priority?.sortOrder ?? 0,
    isActive: priority?.isActive ?? true,
    isDefault: priority?.isDefault ?? false,
  }));

  const save = useMutation({
    mutationFn: async () =>
      priority
        ? taxonomyApi.priorities.update(priority.id, form)
        : taxonomyApi.priorities.create(form),
    onSuccess: () => {
      toast.success(priority ? "Priority updated" : "Priority created");
      onSaved();
    },
    onError: () => toast.error("Save failed"),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{priority ? "Edit priority" : "New priority"}</DialogTitle>
          <DialogDescription>
            Higher <em>level</em> means more severe. Use it for sorting and escalation rules.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <Input
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </Field>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Level">
              <Input
                type="number"
                value={form.level}
                onChange={(e) => setForm((f) => ({ ...f, level: Number(e.target.value) }))}
              />
            </Field>
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
          <div className="flex items-center gap-6">
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <input
                type="checkbox"
                checked={form.isActive}
                onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
              />
              Active
            </label>
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <input
                type="checkbox"
                checked={form.isDefault}
                onChange={(e) => setForm((f) => ({ ...f, isDefault: e.target.checked }))}
              />
              Default priority (pre-selected for new tickets, no row highlight)
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
