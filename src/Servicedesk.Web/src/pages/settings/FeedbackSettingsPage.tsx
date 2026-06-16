import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ClipboardList, Pencil, Plus, Trash2 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import {
  feedbackAdminApi,
  type FeedbackWorkPointType,
  type FeedbackAdminWorkPointTypeInput,
  type FeedbackAdminWorkPointTypeUpdate,
} from "@/lib/api";
import { ApiError } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

const QUERY_KEY = ["feedback", "admin", "work-point-types"] as const;

export function FeedbackSettingsPage() {
  const [editing, setEditing] = React.useState<FeedbackWorkPointType | "new" | null>(null);
  const [includeInactive, setIncludeInactive] = React.useState(true);

  const typesQuery = useQuery({
    queryKey: [...QUERY_KEY, includeInactive],
    queryFn: () => feedbackAdminApi.listWorkPointTypes(includeInactive),
  });
  const types = typesQuery.data?.items ?? [];

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <ClipboardList className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Employee Feedback</h1>
          <p className="max-w-xl text-sm text-muted-foreground">
            Manage work-point types used to categorize employee feedback entries. Each type has a
            name, color indicator, and sort order. Deactivate types that are in use instead of
            deleting them.
          </p>
        </div>
        <Badge className="border border-glass bg-glass text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <WorkPointTypesSection
        types={types}
        loading={typesQuery.isLoading}
        includeInactive={includeInactive}
        onToggleInactive={setIncludeInactive}
        onEdit={(t) => setEditing(t)}
        onNew={() => setEditing("new")}
      />

      {editing && (
        <WorkPointTypeDialog
          item={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
    </div>
  );
}

// ---- Work-point types section ---------------------------------------------

function WorkPointTypesSection({
  types,
  loading,
  includeInactive,
  onToggleInactive,
  onEdit,
  onNew,
}: {
  types: FeedbackWorkPointType[];
  loading: boolean;
  includeInactive: boolean;
  onToggleInactive: (v: boolean) => void;
  onEdit: (t: FeedbackWorkPointType) => void;
  onNew: () => void;
}) {
  const qc = useQueryClient();
  const [confirmDeleteId, setConfirmDeleteId] = React.useState<string | null>(null);

  const deleteMutation = useMutation({
    mutationFn: (id: string) => feedbackAdminApi.deleteWorkPointType(id),
    onSuccess: () => {
      toast.success("Work-point type deleted.");
      qc.invalidateQueries({ queryKey: QUERY_KEY });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        const body = err.body as { error?: string } | null;
        if (body?.error) {
          toast.error(body.error + " — deactivate the type instead of deleting it.");
          return;
        }
      }
      toast.error("Could not delete work-point type.");
    },
  });

  return (
    <section className="glass-card p-6">
      <div className="mb-4 flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="rounded-md bg-glass p-2 text-primary">
            <ClipboardList className="h-5 w-5" />
          </div>
          <div>
            <h2 className="text-base font-semibold text-foreground">Work-point types</h2>
            <p className="text-xs text-muted-foreground">
              Categories for feedback entries. Color is shown as a dot next to the type name in
              the feedback board. Inactive types are hidden from the board dropdown but kept for
              historical entries.
            </p>
          </div>
        </div>
      </div>

      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <label className="flex items-center gap-2 text-xs text-muted-foreground cursor-pointer">
          <Switch checked={includeInactive} onCheckedChange={onToggleInactive} />
          Show inactive
        </label>
        <Button size="sm" onClick={onNew}>
          <Plus className="mr-1.5 h-4 w-4" /> New type
        </Button>
      </div>

      {loading ? (
        <div className="space-y-2">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : (
        <div className="overflow-hidden rounded-md border border-glass">
          <table className="w-full text-left text-sm">
            <thead className="text-xs uppercase tracking-wide text-muted-foreground [&_th]:border-b [&_th]:border-glass [&_th]:bg-glass">
              <tr>
                <th className="px-4 py-3 font-medium">Name</th>
                <th className="px-4 py-3 font-medium">Color</th>
                <th className="px-4 py-3 font-medium">Sort</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {types.map((t) => (
                <tr key={t.id} className="border-b border-glass hover:bg-glass-hover">
                  <td className="px-4 py-3">
                    <span
                      className={cn(
                        "inline-flex items-center gap-2",
                        !t.isActive && "text-muted-foreground",
                      )}
                    >
                      <span
                        className="inline-block h-3 w-3 rounded-full shrink-0"
                        style={{ backgroundColor: t.color }}
                      />
                      {t.name}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <span className="flex items-center gap-2 text-xs font-mono text-muted-foreground">
                      <span
                        className="inline-block h-5 w-5 rounded border border-glass"
                        style={{ backgroundColor: t.color }}
                      />
                      {t.color}
                    </span>
                  </td>
                  <td className="px-4 py-3 font-mono text-xs text-muted-foreground">
                    {t.sortOrder}
                  </td>
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
                  <td className="px-4 py-3">
                    <div className="flex items-center justify-end gap-1">
                      <Button size="sm" variant="ghost" onClick={() => onEdit(t)}>
                        <Pencil className="mr-1 h-3.5 w-3.5" /> Edit
                      </Button>
                      {confirmDeleteId === t.id ? (
                        <>
                          <Button
                            size="sm"
                            variant="destructive"
                            disabled={deleteMutation.isPending}
                            onClick={() => {
                              deleteMutation.mutate(t.id);
                              setConfirmDeleteId(null);
                            }}
                          >
                            Confirm
                          </Button>
                          <Button
                            size="sm"
                            variant="ghost"
                            onClick={() => setConfirmDeleteId(null)}
                          >
                            Cancel
                          </Button>
                        </>
                      ) : (
                        <Button
                          size="sm"
                          variant="ghost"
                          className="text-muted-foreground hover:text-destructive"
                          onClick={() => setConfirmDeleteId(t.id)}
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </Button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
              {types.length === 0 && (
                <tr>
                  <td colSpan={5} className="p-8 text-center text-sm text-muted-foreground">
                    No work-point types defined yet. Click "New type" to add one.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

// ---- Create / edit dialog ------------------------------------------------

function WorkPointTypeDialog({
  item,
  onClose,
}: {
  item: FeedbackWorkPointType | null;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const isNew = item === null;

  const [name, setName] = React.useState(item?.name ?? "");
  const [color, setColor] = React.useState(item?.color ?? "#6366f1");
  const [sortOrder, setSortOrder] = React.useState(item?.sortOrder ?? 0);
  const [isActive, setIsActive] = React.useState(item?.isActive ?? true);
  const [error, setError] = React.useState<string | null>(null);

  const save = useMutation({
    mutationFn: async () => {
      setError(null);
      const trimmed = name.trim();
      if (!trimmed) {
        setError("Name is required.");
        throw new Error("invalid");
      }
      if (isNew) {
        const body: FeedbackAdminWorkPointTypeInput = {
          name: trimmed,
          color,
          sortOrder,
        };
        return feedbackAdminApi.createWorkPointType(body);
      } else {
        const body: FeedbackAdminWorkPointTypeUpdate = {
          name: trimmed,
          color,
          sortOrder,
          isActive,
        };
        return feedbackAdminApi.updateWorkPointType(item.id, body);
      }
    },
    onSuccess: () => {
      toast.success(isNew ? "Work-point type created." : "Work-point type updated.");
      qc.invalidateQueries({ queryKey: QUERY_KEY });
      onClose();
    },
    onError: (err) => {
      if (err instanceof Error && err.message === "invalid") return;
      if (err instanceof ApiError) {
        const body = err.body as { error?: string } | null;
        if (body?.error) {
          setError(body.error);
          return;
        }
      }
      setError("Could not save work-point type.");
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{isNew ? "New work-point type" : "Edit work-point type"}</DialogTitle>
          <DialogDescription>
            Work-point types appear in the feedback board dropdown. The color dot makes entries
            visually scannable at a glance. Deactivate rather than delete types that are in use.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <label className="block text-xs">
            <span className="mb-1 block text-muted-foreground">Name</span>
            <Input
              value={name}
              autoFocus
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Communication"
              onKeyDown={(e) => { if (e.key === "Enter") save.mutate(); }}
            />
          </label>

          <div className="flex items-end gap-4">
            <label className="block flex-1 text-xs">
              <span className="mb-1 block text-muted-foreground">Color</span>
              <div className="flex items-center gap-2">
                <input
                  type="color"
                  value={color}
                  onChange={(e) => setColor(e.target.value)}
                  className="h-8 w-10 cursor-pointer rounded-md border border-glass bg-glass p-0.5"
                />
                <Input
                  value={color}
                  onChange={(e) => setColor(e.target.value)}
                  placeholder="#6366f1"
                  className="h-8 font-mono text-sm"
                />
              </div>
            </label>

            <label className="block text-xs">
              <span className="mb-1 block text-muted-foreground">Sort order</span>
              <Input
                type="number"
                value={sortOrder}
                onChange={(e) => setSortOrder(Number(e.target.value))}
                className="h-8 w-24 text-sm"
                min={0}
              />
            </label>
          </div>

          {!isNew && (
            <label className="flex items-center justify-between rounded-md border border-glass bg-glass px-3 py-2 text-xs cursor-pointer">
              <span>
                <span className="block text-foreground">Active</span>
                <span className="text-muted-foreground">
                  Inactive types are hidden from the board but preserved for existing entries.
                </span>
              </span>
              <Switch checked={isActive} onCheckedChange={setIsActive} />
            </label>
          )}

          {error && (
            <p className="text-sm text-destructive">{error}</p>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending}>
            {isNew ? "Create" : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
