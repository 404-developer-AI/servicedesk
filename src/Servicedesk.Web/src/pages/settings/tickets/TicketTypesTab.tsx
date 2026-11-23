import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { taxonomyApi, type TicketType, type TicketTypeInput } from "@/lib/taxonomy-api";
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
import { errorMessage } from "@/lib/http";

export function TicketTypesTab() {
  const confirm = useConfirm();
  const qc = useQueryClient();
  const [editing, setEditing] = useState<TicketType | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "ticket-types"],
    queryFn: () => taxonomyApi.ticketTypes.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.ticketTypes.remove(id),
    ["taxonomy", "ticket-types"],
    "Ticket type",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New ticket type</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Label</th>
              <th className="px-4 py-3 font-medium">Code</th>
              <th className="px-4 py-3 font-medium">Icon</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Order</th>
              <th className="px-4 py-3 font-medium">Status</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((t) => (
              <tr key={t.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">
                  <div className="flex items-center gap-2">
                    {t.label}
                    {t.isSystem && (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        system
                      </Badge>
                    )}
                  </div>
                  {t.description && (
                    <div className="text-xs text-muted-foreground">{t.description}</div>
                  )}
                </td>
                <td className="px-4 py-3">
                  <span className="font-mono text-[11px] text-muted-foreground">{t.code}</span>
                </td>
                <td className="px-4 py-3">
                  <span className="font-mono text-[11px] text-muted-foreground">{t.icon}</span>
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={t.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{t.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-muted-foreground">{t.sortOrder}</td>
                <td className="px-4 py-3">
                  {t.isActive ? (
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
                  <Button variant="ghost" size="sm" onClick={() => setEditing(t)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={del.isPending || t.isSystem}
                    onClick={async () => {
                      if (
                        await confirm({
                          title: "Delete ticket type",
                          description: `Delete ticket type "${t.label}"?`,
                          confirmLabel: "Delete",
                          tone: "destructive",
                        })
                      )
                        del.mutate(t.id);
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
                  No ticket types yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <TicketTypeDialog
          ticketType={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "ticket-types"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function TicketTypeDialog({
  ticketType,
  onClose,
  onSaved,
}: {
  ticketType: TicketType | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<TicketTypeInput>(() => ({
    code: ticketType?.code ?? "",
    label: ticketType?.label ?? "",
    description: ticketType?.description ?? "",
    icon: ticketType?.icon ?? "ticket",
    color: ticketType?.color ?? "#7c7cff",
    sortOrder: ticketType?.sortOrder ?? 0,
    isActive: ticketType?.isActive ?? true,
  }));

  const save = useMutation({
    mutationFn: async () => {
      if (ticketType) return taxonomyApi.ticketTypes.update(ticketType.id, form);
      return taxonomyApi.ticketTypes.create(form);
    },
    onSuccess: () => {
      toast.success(ticketType ? "Ticket type updated" : "Ticket type created");
      onSaved();
    },
    onError: (err) => {
      toast.error(errorMessage(err, "Save failed"));
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{ticketType ? "Edit ticket type" : "New ticket type"}</DialogTitle>
          <DialogDescription>
            First-class ticket type. Codes are lowercase slugs (e.g. <span className="font-mono">order</span>). Icons are{" "}
            <a
              href="https://lucide.dev/icons/"
              target="_blank"
              rel="noopener noreferrer"
              className="text-primary hover:underline"
            >
              lucide icon names
            </a>{" "}
            (e.g. <span className="font-mono">shopping-cart</span>). The chosen color appears as a badge accent on every ticket of this type.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <div className="grid grid-cols-2 gap-3">
            <Field label="Code">
              <Input
                value={form.code}
                placeholder="order"
                disabled={ticketType?.isSystem}
                onChange={(e) => setForm((f) => ({ ...f, code: e.target.value }))}
              />
            </Field>
            <Field label="Label">
              <Input
                value={form.label}
                placeholder="Order"
                onChange={(e) => setForm((f) => ({ ...f, label: e.target.value }))}
              />
            </Field>
          </div>
          <Field label="Description">
            <Input
              value={form.description ?? ""}
              placeholder="Procurement or hardware order request"
              onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))}
            />
          </Field>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Icon">
              <Input
                value={form.icon ?? ""}
                placeholder="shopping-cart"
                onChange={(e) => setForm((f) => ({ ...f, icon: e.target.value }))}
              />
            </Field>
            <Field label="Color">
              <div className="flex items-center gap-2">
                <input
                  type="color"
                  value={form.color ?? "#7c7cff"}
                  onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                  className="h-9 w-12 cursor-pointer rounded-md border border-glass bg-transparent"
                />
                <Input
                  value={form.color ?? ""}
                  onChange={(e) => setForm((f) => ({ ...f, color: e.target.value }))}
                  className="font-mono text-xs"
                />
              </div>
            </Field>
            <Field label="Sort order">
              <Input
                type="number"
                value={form.sortOrder}
                onChange={(e) => setForm((f) => ({ ...f, sortOrder: Number(e.target.value) }))}
              />
            </Field>
          </div>
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))}
            />
            Active (visible in the "Create linked ticket" picker when bound to a manual trigger)
          </label>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending}>
            {save.isPending ? "Saving…" : ticketType ? "Save" : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
