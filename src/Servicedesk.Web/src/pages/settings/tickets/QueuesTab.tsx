import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ApiError,
  graphAdminApi,
  taxonomyApi,
  type GraphMailFolder,
  type Queue,
  type QueueInput,
} from "@/lib/api";
import { Plus, Trash2, Mail } from "lucide-react";
import { Switch } from "@/components/ui/switch";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
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

export function QueuesTab() {
  const confirm = useConfirm();
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Queue | null | "new">(null);

  const { data, isLoading } = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });

  const del = useDeleteHandler(
    (id) => taxonomyApi.queues.remove(id),
    ["taxonomy", "queues"],
    "Queue",
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing("new")}>+ New queue</Button>
      </div>
      {isLoading ? (
        <LoadingSkeleton />
      ) : (
        <TableShell>
          <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass">
            <tr>
              <th className="px-4 py-3 font-medium">Name</th>
              <th className="px-4 py-3 font-medium">Color</th>
              <th className="px-4 py-3 font-medium">Order</th>
              <th className="px-4 py-3 font-medium">Status</th>
              <th className="px-4 py-3" />
            </tr>
          </thead>
          <tbody>
            {data?.map((q) => (
              <tr key={q.id} className="border-b border-glass hover:bg-glass-hover">
                <td className="px-4 py-3 text-foreground">
                  <div className="flex items-center gap-2">
                    {q.name}
                  </div>
                  {q.description && (
                    <div className="text-xs text-muted-foreground">{q.description}</div>
                  )}
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <ColorSwatch color={q.color} />
                    <span className="font-mono text-[11px] text-muted-foreground">{q.color}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-muted-foreground">{q.sortOrder}</td>
                <td className="px-4 py-3">
                  {q.isActive ? (
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
                  <Button variant="ghost" size="sm" onClick={() => setEditing(q)}>
                    Edit
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={del.isPending}
                    onClick={async () => {
                      if (
                        await confirm({
                          title: "Delete queue",
                          description: `Delete queue "${q.name}"?`,
                          confirmLabel: "Delete",
                          tone: "destructive",
                        })
                      )
                        del.mutate(q.id);
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
                  No queues yet.
                </td>
              </tr>
            )}
          </tbody>
        </TableShell>
      )}

      {editing && (
        <QueueDialog
          queue={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            qc.invalidateQueries({ queryKey: ["taxonomy", "queues"] });
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

/// One editable inbound-mailbox source in the queue dialog. `key` is a stable
/// client-side id so per-row folder lists / loading state survive re-renders;
/// `id` is the server source id (absent for newly-added rows).
type InboundRow = {
  key: string;
  id?: string;
  mailbox: string;
  folderId: string;
  folderName: string;
  pollingEnabled: boolean;
};

function QueueDialog({
  queue,
  onClose,
  onSaved,
}: {
  queue: Queue | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState<QueueInput>(() => ({
    name: queue?.name ?? "",
    description: queue?.description ?? "",
    color: queue?.color ?? "#7c7cff",
    icon: queue?.icon ?? "inbox",
    sortOrder: queue?.sortOrder ?? 0,
    isActive: queue?.isActive ?? true,
    outboundMailboxAddress: queue?.outboundMailboxAddress ?? "",
    allowedStatusIds: queue?.allowedStatusIds ?? [],
    defaultStatusId: queue?.defaultStatusId ?? null,
    aiAssistEnabled: queue?.aiAssistEnabled ?? true,
    timeAlertMode: queue?.timeAlertMode ?? "inherit",
    timeAlertThresholdMinutes: queue?.timeAlertThresholdMinutes ?? null,
  }));

  // v0.0.66 — a queue can have many inbound mailbox sources. Each editable row
  // carries a stable client-side `key` so its folder list / loading state is
  // tracked independently of server ids (new rows have no id yet).
  const [rows, setRows] = useState<InboundRow[]>(() =>
    (queue?.inboundMailboxes ?? []).map((m) => ({
      key: crypto.randomUUID(),
      id: m.id,
      mailbox: m.mailbox ?? "",
      folderId: m.folderId ?? "",
      folderName: m.folderName ?? "",
      pollingEnabled: m.pollingEnabled,
    })),
  );

  // v0.0.40 polish — load every status so the "Status scope" block
  // can offer the multi-select. Cached + reused with the existing key.
  const { data: statuses } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
    staleTime: 60_000,
  });

  const [folders, setFolders] = useState<Record<string, GraphMailFolder[]>>({});
  const [foldersLoading, setFoldersLoading] = useState<Record<string, boolean>>({});
  const [foldersError, setFoldersError] = useState<Record<string, string | null>>({});

  const updateRow = (key: string, patch: Partial<InboundRow>) =>
    setRows((rs) => rs.map((r) => (r.key === key ? { ...r, ...patch } : r)));

  const addRow = () =>
    setRows((rs) => [
      ...rs,
      { key: crypto.randomUUID(), mailbox: "", folderId: "", folderName: "", pollingEnabled: true },
    ]);

  const removeRow = (key: string) => setRows((rs) => rs.filter((r) => r.key !== key));

  const loadFolders = async (row: InboundRow) => {
    const mailbox = row.mailbox.trim();
    if (!mailbox) return;
    setFoldersLoading((s) => ({ ...s, [row.key]: true }));
    setFoldersError((s) => ({ ...s, [row.key]: null }));
    try {
      const result = await graphAdminApi.listFolders(mailbox);
      if (Array.isArray(result)) {
        setFolders((s) => ({ ...s, [row.key]: result }));
      } else {
        setFoldersError((s) => ({
          ...s,
          [row.key]: (result as { error?: string }).error ?? "Unknown error",
        }));
      }
    } catch (err) {
      setFoldersError((s) => ({
        ...s,
        [row.key]: err instanceof ApiError ? `Failed (${err.status})` : "Failed to load folders",
      }));
    } finally {
      setFoldersLoading((s) => ({ ...s, [row.key]: false }));
    }
  };

  // Every row must have both a mailbox and a selected folder before saving.
  const rowsComplete = rows.every((r) => r.mailbox.trim() && r.folderId.trim());

  const save = useMutation({
    mutationFn: async () => {
      const payload: QueueInput = {
        ...form,
        outboundMailboxAddress: form.outboundMailboxAddress?.trim() || null,
        inboundMailboxes: rows.map((r) => ({
          id: r.id ?? null,
          mailbox: r.mailbox.trim(),
          folderId: r.folderId.trim() || null,
          folderName: r.folderName.trim() || null,
          pollingEnabled: r.pollingEnabled,
        })),
        allowedStatusIds: form.allowedStatusIds ?? [],
        defaultStatusId: form.defaultStatusId ?? null,
      };
      if (queue) {
        return taxonomyApi.queues.update(queue.id, payload);
      }
      return taxonomyApi.queues.create(payload);
    },
    onSuccess: () => {
      toast.success(queue ? "Queue updated" : "Queue created");
      onSaved();
    },
    onError: (err) => {
      toast.error(
        err instanceof ApiError
          ? err.status === 409
            ? "A mailbox/folder is already assigned to another queue"
            : `Save failed (${err.status})`
          : "Save failed",
      );
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{queue ? "Edit queue" : "New queue"}</DialogTitle>
          <DialogDescription>
            Queues are the inboxes tickets land in. Name it after a team or workstream.
          </DialogDescription>
        </DialogHeader>
        <div className="max-h-[70vh] space-y-3 overflow-y-auto pr-1 text-sm">
          <Field label="Name">
            <Input
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </Field>
          <Field label="Description">
            <Input
              value={form.description ?? ""}
              onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))}
            />
          </Field>
          <div className="grid grid-cols-2 gap-3">
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
            Active (visible to agents when creating tickets)
          </label>
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={form.aiAssistEnabled ?? true}
              onChange={(e) => setForm((f) => ({ ...f, aiAssistEnabled: e.target.checked }))}
            />
            AI assist (show "Analyze &amp; propose a solution by AI" on tickets in this queue)
          </label>

          {/* v0.0.87 — per-queue override for the ticket hour-limit alert.
              "Inherit" uses the global Settings → Timesheet switch + limit;
              On/Off force it for this queue; the limit override replaces the
              global limit for tickets in this queue only. */}
          <div className="space-y-3 rounded-md border border-glass-strong bg-glass p-3">
            <div className="text-xs font-medium uppercase tracking-wider text-muted-foreground/70">
              Hour-limit alert
            </div>
            <Field label="Mode">
              <select
                value={form.timeAlertMode ?? "inherit"}
                onChange={(e) =>
                  setForm((f) => ({
                    ...f,
                    timeAlertMode: e.target.value as "inherit" | "on" | "off",
                  }))
                }
                className="h-9 w-full rounded-md border border-glass bg-glass px-2 text-sm text-foreground outline-hidden focus-visible:ring-2 focus-visible:ring-ring"
              >
                <option value="inherit">Inherit global setting</option>
                <option value="on">Always on for this queue</option>
                <option value="off">Off for this queue</option>
              </select>
            </Field>
            {form.timeAlertMode !== "off" && (
              <Field label="Limit override (minutes, empty = global)">
                <Input
                  type="number"
                  min={1}
                  value={form.timeAlertThresholdMinutes ?? ""}
                  onChange={(e) => {
                    const v = e.target.value.trim();
                    const n = v === "" ? null : Number.parseInt(v, 10);
                    setForm((f) => ({
                      ...f,
                      timeAlertThresholdMinutes:
                        n !== null && Number.isFinite(n) && n > 0 ? n : null,
                    }));
                  }}
                  placeholder="Use global limit"
                  className="font-mono"
                />
              </Field>
            )}
            <p className="text-[11px] text-muted-foreground/70">
              Controls the per-ticket hour-limit warning for tickets in this
              queue. The queue active on a ticket when it is opened (or its queue
              is changed) decides which rule applies.
            </p>
          </div>

          {/* v0.0.40 polish — status scope. Leave empty to keep the
              current behaviour (all statuses available); pick a subset
              to filter the status dropdown and the trigger set_status
              picker. Default status is the auto-flip target when a
              ticket moves into this queue on a status not in the list. */}
          <div className="space-y-3 rounded-md border border-glass-strong bg-glass p-3">
            <div className="text-xs font-medium uppercase tracking-wider text-muted-foreground/70">
              Status scope
            </div>
            <Field label="Allowed statuses (leave empty = all)">
              <div className="flex flex-wrap gap-1.5">
                {(statuses ?? []).filter((s) => s.isActive).map((s) => {
                  const selected = form.allowedStatusIds?.includes(s.id) ?? false;
                  return (
                    <button
                      key={s.id}
                      type="button"
                      onClick={() =>
                        setForm((f) => {
                          const current = f.allowedStatusIds ?? [];
                          const next = selected
                            ? current.filter((id) => id !== s.id)
                            : [...current, s.id];
                          return {
                            ...f,
                            allowedStatusIds: next,
                            // If the current defaultStatusId is no
                            // longer in the (non-empty) list, clear it.
                            defaultStatusId:
                              next.length > 0 && f.defaultStatusId
                                ? next.includes(f.defaultStatusId) ? f.defaultStatusId : null
                                : f.defaultStatusId,
                          };
                        })
                      }
                      className={cn(
                        "rounded-md border px-2 py-0.5 text-[11px] transition",
                        selected
                          ? "border-violet-400/40 bg-violet-400/15 text-violet-200"
                          : "border-glass bg-glass text-muted-foreground hover:bg-glass-hover",
                      )}
                      style={selected ? { color: s.color } : undefined}
                    >
                      {s.name}
                    </button>
                  );
                })}
              </div>
            </Field>
            <Field label="Default status on queue change">
              <Select
                value={form.defaultStatusId ?? "__none__"}
                onValueChange={(v) =>
                  setForm((f) => ({
                    ...f,
                    defaultStatusId: v === "__none__" ? null : v,
                  }))
                }
              >
                <SelectTrigger className="h-9 border-glass bg-glass">
                  <SelectValue placeholder="— no auto-flip —" />
                </SelectTrigger>
                <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
                  <SelectItem value="__none__">— no auto-flip —</SelectItem>
                  {(statuses ?? [])
                    .filter((s) => s.isActive)
                    .filter((s) => (form.allowedStatusIds?.length ?? 0) === 0 || form.allowedStatusIds!.includes(s.id))
                    .map((s) => (
                      <SelectItem key={s.id} value={s.id}>
                        {s.name}
                      </SelectItem>
                    ))}
                </SelectContent>
              </Select>
            </Field>
            <p className="text-[11px] text-muted-foreground/70">
              When a ticket moves into this queue (manually or via a trigger) and its current status is not allowed here, the status auto-flips to the default. Leave both empty to disable.
            </p>
          </div>

          <div className="space-y-3 rounded-md border border-glass-strong bg-glass p-3">
            <div className="flex items-center justify-between">
              <div className="text-xs font-medium uppercase tracking-wider text-muted-foreground/70">
                Microsoft Graph mailboxes
              </div>
              <Button
                type="button"
                size="sm"
                variant="outline"
                className="h-7 shrink-0 gap-1 border-glass bg-glass px-2 text-xs hover:bg-glass-hover"
                onClick={addRow}
              >
                <Plus className="h-3.5 w-3.5" /> Add mailbox
              </Button>
            </div>

            {rows.length === 0 && (
              <div className="rounded-md border border-dashed border-glass-strong bg-glass px-4 py-5 text-center text-xs text-muted-foreground">
                No inbound mailboxes. Add one to poll mail into this queue.
              </div>
            )}

            {rows.map((row) => {
              const rowFolders = folders[row.key] ?? [];
              const rowLoading = !!foldersLoading[row.key];
              const rowError = foldersError[row.key];
              const hasMailbox = !!row.mailbox.trim();
              const hasFolder = !!row.folderId.trim();
              return (
                <div
                  key={row.key}
                  className="space-y-3 rounded-md border border-glass bg-glass-hover/30 p-3"
                >
                  <div className="flex items-center gap-2">
                    <Mail className="h-4 w-4 shrink-0 text-muted-foreground" />
                    <Input
                      type="email"
                      placeholder="support@company.com"
                      value={row.mailbox}
                      onChange={(e) =>
                        updateRow(row.key, { mailbox: e.target.value })
                      }
                      className="flex-1"
                    />
                    <label className="flex shrink-0 items-center gap-1.5 text-[11px] text-muted-foreground">
                      <Switch
                        checked={row.pollingEnabled}
                        onCheckedChange={(checked) =>
                          updateRow(row.key, { pollingEnabled: checked })
                        }
                        aria-label="Toggle polling for this mailbox"
                      />
                      Polling
                    </label>
                    <Button
                      type="button"
                      size="icon"
                      variant="ghost"
                      className="h-8 w-8 shrink-0 text-muted-foreground hover:text-destructive"
                      onClick={() => removeRow(row.key)}
                      aria-label="Remove mailbox"
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>

                  <div className="flex items-center gap-2">
                    <Select
                      value={row.folderId || undefined}
                      disabled={!hasMailbox}
                      onValueChange={(v) => {
                        const selected = rowFolders.find((f) => f.id === v);
                        updateRow(row.key, {
                          folderId: v,
                          folderName: selected?.displayName ?? row.folderName,
                        });
                      }}
                    >
                      <SelectTrigger className="h-9 flex-1 border-glass bg-glass transition-colors focus:border-glass-strong focus:bg-glass-strong">
                        <SelectValue placeholder="Select a folder…">
                          {row.folderName || row.folderId || "Select a folder…"}
                        </SelectValue>
                      </SelectTrigger>
                      <SelectContent className="border-glass bg-popover/80 backdrop-blur-xl">
                        {rowFolders.map((f) => (
                          <SelectItem key={f.id} value={f.id}>
                            <span className="flex items-center gap-2">
                              {f.displayName}
                              <span className="text-xs text-muted-foreground">
                                ({f.totalItemCount})
                              </span>
                            </span>
                          </SelectItem>
                        ))}
                        {rowFolders.length === 0 && row.folderId && (
                          <SelectItem value={row.folderId}>
                            {row.folderName || row.folderId}
                          </SelectItem>
                        )}
                        {rowFolders.length === 0 && !row.folderId && (
                          <div className="px-2 py-1.5 text-xs text-muted-foreground">
                            Click "Load folders" to fetch available folders.
                          </div>
                        )}
                      </SelectContent>
                    </Select>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      className="shrink-0 border-glass bg-glass hover:bg-glass-hover"
                      disabled={!hasMailbox || rowLoading}
                      onClick={() => loadFolders(row)}
                    >
                      {rowLoading ? "Loading…" : "Load folders"}
                    </Button>
                  </div>
                  {rowError && <p className="text-xs text-red-400">{rowError}</p>}
                  {hasMailbox && !hasFolder && (
                    <p className="text-xs text-amber-400">
                      Select a folder for this mailbox.
                    </p>
                  )}
                </div>
              );
            })}

            <Field label="Outbound mailbox address (send-as for replies)">
              <Input
                type="email"
                placeholder="support@company.com"
                value={form.outboundMailboxAddress ?? ""}
                onChange={(e) =>
                  setForm((f) => ({ ...f, outboundMailboxAddress: e.target.value }))
                }
              />
            </Field>

            <p className="text-xs text-muted-foreground">
              Each inbound mailbox is polled from its selected folder by Microsoft
              Graph and routed to this queue. A mailbox/folder can only feed one
              queue. Replies are always sent from the outbound address above.
            </p>
          </div>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !form.name || !rowsComplete}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
