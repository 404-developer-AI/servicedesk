import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Archive, ArchiveRestore, Bed, Clock, ListChecks, Pencil, Plus, Ticket } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { ApiError, apiErrorMessage } from "@/lib/http";
import { timesheetTaskApi, type TimesheetTask, type TimesheetTaskUpsert } from "@/lib/timesheet-api";
import { cn } from "@/lib/utils";

// ---- Tasks catalogue (merged from the standalone tasks-settings page) -

/// Admin CRUD for the timesheet-task catalogue. Drives the Tab-1 dropdown
/// on the agent side. Archive instead of delete so historical entries
/// that still reference a retired task keep rendering correctly (FK has
/// `ON DELETE RESTRICT`).
export function TasksSection() {
  const [editing, setEditing] = React.useState<TimesheetTask | "new" | null>(null);
  const [includeArchived, setIncludeArchived] = React.useState(true);

  const tasksQuery = useQuery({
    queryKey: ["timesheet", "admin", "tasks", includeArchived],
    queryFn: () => timesheetTaskApi.list(includeArchived),
  });
  const tasks = tasksQuery.data ?? [];

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="rounded-md bg-glass p-2 text-primary">
            <ListChecks className="h-5 w-5" />
          </div>
          <div>
            <h2 className="text-base font-semibold text-foreground">Tasks</h2>
            <p className="text-xs text-muted-foreground">
              The catalogue agents pick from when registering time. Each task can require a
              ticket (Servicedesk, Project) or stand alone (Administratie, Verlof). Absence
              tasks roll up separately in the month overview.
            </p>
          </div>
        </div>
      </div>

      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <Switch checked={includeArchived} onCheckedChange={setIncludeArchived} />
          Show archived
        </label>
        <Button size="sm" onClick={() => setEditing("new")}>
          <Plus className="mr-1.5 h-4 w-4" /> New task
        </Button>
      </div>

      {tasksQuery.isLoading ? (
        <div className="space-y-2">
          {[...Array(3)].map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : (
        <div className="overflow-hidden rounded-md border border-glass">
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass [&_th]:bg-glass">
              <tr>
                <th className="px-4 py-3 font-medium">Name</th>
                <th className="px-4 py-3 font-medium">Requires ticket</th>
                <th className="px-4 py-3 font-medium">Absence</th>
                <th className="px-4 py-3 font-medium">Sort</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {tasks.map((t) => (
                <tr key={t.id} className="border-b border-glass hover:bg-glass-hover">
                  <td className="px-4 py-3 text-foreground">
                    <span className={cn("inline-flex items-center gap-2", t.archived && "text-muted-foreground line-through")}>
                      <Clock className="h-3.5 w-3.5 text-muted-foreground" />
                      {t.name}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-xs">
                    {t.requiresTicket ? (
                      <span className="inline-flex items-center gap-1 text-emerald-300">
                        <Ticket className="h-3 w-3" /> required
                      </span>
                    ) : (
                      <span className="text-muted-foreground">no ticket</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-xs">
                    {t.isAbsence ? (
                      <span className="inline-flex items-center gap-1 text-amber-300">
                        <Bed className="h-3 w-3" /> absence
                      </span>
                    ) : (
                      <span className="text-muted-foreground">—</span>
                    )}
                  </td>
                  <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{t.sortOrder}</td>
                  <td className="px-4 py-3">
                    {t.archived ? (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        archived
                      </Badge>
                    ) : (
                      <Badge className="border border-emerald-400/20 bg-emerald-400/10 text-[10px] font-normal text-emerald-200">
                        active
                      </Badge>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Button size="sm" variant="ghost" onClick={() => setEditing(t)}>
                      <Pencil className="mr-1 h-3.5 w-3.5" /> Edit
                    </Button>
                  </td>
                </tr>
              ))}
              {tasks.length === 0 && (
                <tr>
                  <td colSpan={6} className="p-8 text-center text-sm text-muted-foreground">
                    No tasks defined yet. The bootstrap seeds a default set; if you see this
                    on a fresh install, restart the API.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {editing && (
        <TaskDialog
          task={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
    </section>
  );
}

function TaskDialog({ task, onClose }: { task: TimesheetTask | null; onClose: () => void }) {
  const qc = useQueryClient();
  const [form, setForm] = React.useState<TimesheetTaskUpsert>(() => ({
    name: task?.name ?? "",
    requiresTicket: task?.requiresTicket ?? true,
    isAbsence: task?.isAbsence ?? false,
    archived: task?.archived ?? false,
    sortOrder: task?.sortOrder ?? 0,
  }));
  const [error, setError] = React.useState<string | null>(null);

  const save = useMutation({
    mutationFn: async () => {
      setError(null);
      const trimmedName = form.name.trim();
      if (trimmedName.length === 0) {
        setError("Name is required.");
        throw new Error("invalid");
      }
      const body: TimesheetTaskUpsert = { ...form, name: trimmedName };
      if (task === null) return await timesheetTaskApi.create(body);
      return await timesheetTaskApi.update(task.id, body);
    },
    onSuccess: () => {
      toast.success(task === null ? "Task created" : "Task updated");
      qc.invalidateQueries({ queryKey: ["timesheet", "tasks"] });
      qc.invalidateQueries({ queryKey: ["timesheet", "admin", "tasks"] });
      onClose();
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        setError(apiErrorMessage(err) ?? "Failed to save");
      } else if (err instanceof Error && err.message !== "invalid") {
        setError(err.message);
      }
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{task ? "Edit task" : "New task"}</DialogTitle>
          <DialogDescription>
            Names must be unique among active tasks. Archive a task instead of deleting so
            historical entries keep their reference.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <label className="block text-xs">
            <span className="mb-1 block text-muted-foreground">Name</span>
            <Input
              value={form.name}
              autoFocus
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              placeholder="e.g. Servicedesk"
            />
          </label>

          <div className="grid grid-cols-2 gap-3">
            <label className="flex items-center justify-between rounded-md border border-glass bg-glass px-3 py-2 text-xs">
              <span>
                <span className="block text-foreground">Requires ticket</span>
                <span className="text-muted-foreground">Agent must link a ticket.</span>
              </span>
              <Switch
                checked={form.requiresTicket}
                onCheckedChange={(v) => setForm({ ...form, requiresTicket: v })}
              />
            </label>
            <label className="flex items-center justify-between rounded-md border border-glass bg-glass px-3 py-2 text-xs">
              <span>
                <span className="block text-foreground">Absence</span>
                <span className="text-muted-foreground">Rolls up separately.</span>
              </span>
              <Switch
                checked={form.isAbsence}
                onCheckedChange={(v) => setForm({ ...form, isAbsence: v })}
              />
            </label>
          </div>

          <label className="block text-xs">
            <span className="mb-1 block text-muted-foreground">Sort order (lower = first)</span>
            <Input
              type="number"
              value={form.sortOrder ?? 0}
              onChange={(e) => setForm({ ...form, sortOrder: Number(e.target.value) })}
            />
          </label>

          {task !== null && (
            <label className="flex items-center justify-between rounded-md border border-glass bg-glass px-3 py-2 text-xs">
              <span className="inline-flex items-center gap-2 text-foreground">
                {form.archived ? <ArchiveRestore className="h-3.5 w-3.5" /> : <Archive className="h-3.5 w-3.5" />}
                {form.archived ? "Archived (hidden from pickers)" : "Active"}
              </span>
              <Switch
                checked={!form.archived}
                onCheckedChange={(v) => setForm({ ...form, archived: !v })}
              />
            </label>
          )}

          {error && <p className="text-sm text-red-300">{error}</p>}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending}>
            {save.isPending ? "Saving…" : task ? "Save" : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
