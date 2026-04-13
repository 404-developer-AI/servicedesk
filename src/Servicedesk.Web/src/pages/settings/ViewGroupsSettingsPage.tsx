import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Plus, Trash2, Users } from "lucide-react";
import {
  viewGroupApi,
  type ViewGroupSummary,
  type ViewGroupDetail,
  type ViewGroupInput,
} from "@/lib/api";
import { userApi, viewApi, type AgentUser, type View } from "@/lib/ticket-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { cn } from "@/lib/utils";

// ---- Group form dialog (create / edit) ----

function GroupFormDialog({
  group,
  onClose,
  onSaved,
}: {
  group: ViewGroupSummary | null;
  onClose: () => void;
  onSaved: (saved: ViewGroupSummary) => void;
}) {
  const [name, setName] = React.useState(group?.name ?? "");
  const [description, setDescription] = React.useState(group?.description ?? "");

  const save = useMutation({
    mutationFn: (): Promise<ViewGroupSummary> => {
      const input: ViewGroupInput = { name: name.trim(), description: description.trim() };
      if (group) return viewGroupApi.update(group.id, input);
      return viewGroupApi.create(input);
    },
    onSuccess: (saved) => {
      toast.success(group ? "Group updated" : "Group created");
      onSaved(saved);
    },
    onError: () => {
      toast.error("Failed to save group");
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{group ? "Edit group" : "New view group"}</DialogTitle>
          <DialogDescription>
            {group
              ? "Update the group name or description."
              : "Create a named bundle of views to assign to agents."}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">Name</label>
            <Input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Support Team Views"
              autoFocus
            />
          </div>
          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">
              Description <span className="text-muted-foreground/50">(optional)</span>
            </label>
            <Input
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Short description of this group"
            />
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => save.mutate()}
            disabled={save.isPending || !name.trim()}
          >
            {save.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Delete confirm dialog ----

function DeleteDialog({
  group,
  onClose,
  onDeleted,
}: {
  group: ViewGroupSummary;
  onClose: () => void;
  onDeleted: () => void;
}) {
  const del = useMutation({
    mutationFn: () => viewGroupApi.remove(group.id),
    onSuccess: () => {
      toast.success("Group deleted");
      onDeleted();
    },
    onError: () => {
      toast.error("Failed to delete group");
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Delete group</DialogTitle>
          <DialogDescription>
            Delete &ldquo;{group.name}&rdquo;? Members will lose access to views granted
            through this group. This cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="destructive" disabled={del.isPending} onClick={() => del.mutate()}>
            {del.isPending ? "Deleting..." : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---- Chip list with add/remove ----

function ChipList<T extends { id: string; label: string }>({
  title,
  items,
  selected,
  available,
  onAdd,
  onRemove,
  saving,
}: {
  title: string;
  items: T[];
  selected: string[];
  available: T[];
  onAdd: (id: string) => void;
  onRemove: (id: string) => void;
  saving: boolean;
}) {
  const [adding, setAdding] = React.useState(false);
  const unselected = available.filter((a) => !selected.includes(a.id));

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
          {title}
        </h3>
        {unselected.length > 0 && (
          <button
            type="button"
            onClick={() => setAdding((p) => !p)}
            className="inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
          >
            <Plus className="h-3 w-3" />
            Add
          </button>
        )}
      </div>

      {adding && unselected.length > 0 && (
        <div className="flex flex-wrap gap-1.5 rounded-md border border-white/[0.08] bg-white/[0.02] p-2">
          {unselected.map((item) => (
            <button
              key={item.id}
              type="button"
              disabled={saving}
              onClick={() => {
                onAdd(item.id);
                setAdding(false);
              }}
              className="inline-flex items-center rounded-full border border-white/10 bg-white/[0.04] px-2.5 py-0.5 text-[11px] text-muted-foreground hover:border-primary/30 hover:bg-primary/10 hover:text-primary transition-colors disabled:opacity-50"
            >
              {item.label}
            </button>
          ))}
        </div>
      )}

      {selected.length === 0 ? (
        <p className="text-xs text-muted-foreground italic px-0.5">None assigned yet.</p>
      ) : (
        <div className="flex flex-wrap gap-1.5">
          {selected.map((id) => {
            const item = items.find((i) => i.id === id);
            if (!item) return null;
            return (
              <span
                key={id}
                className="inline-flex items-center gap-1.5 rounded-full border border-primary/30 bg-primary/10 px-2.5 py-0.5 text-[11px] text-primary"
              >
                {item.label}
                <button
                  type="button"
                  disabled={saving}
                  onClick={() => onRemove(id)}
                  className="ml-0.5 opacity-60 hover:opacity-100 disabled:cursor-not-allowed transition-opacity"
                  aria-label={`Remove ${item.label}`}
                >
                  ×
                </button>
              </span>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ---- Group detail panel ----

function GroupDetailPanel({
  group,
  agents,
  views,
  onEdit,
  onDelete,
}: {
  group: ViewGroupSummary;
  agents: AgentUser[];
  views: View[];
  onEdit: () => void;
  onDelete: () => void;
}) {
  const qc = useQueryClient();

  const { data: detail, isLoading } = useQuery({
    queryKey: ["view-group", group.id],
    queryFn: () => viewGroupApi.get(group.id),
  });

  const memberIds = (detail?.members ?? []).map((m) => m.userId);
  const viewIds = (detail?.views ?? []).map((v) => v.viewId);

  const agentItems = agents.map((a) => ({ id: a.id, label: a.email }));
  const viewItems = views.map((v) => ({ id: v.id, label: v.name }));

  const setMembers = useMutation({
    mutationFn: (ids: string[]) => viewGroupApi.setMembers(group.id, ids),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["view-group", group.id] });
      qc.invalidateQueries({ queryKey: ["view-groups"] });
      toast.success("Members updated");
    },
    onError: () => toast.error("Failed to update members"),
  });

  const setViews = useMutation({
    mutationFn: (ids: string[]) => viewGroupApi.setViews(group.id, ids),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["view-group", group.id] });
      qc.invalidateQueries({ queryKey: ["view-groups"] });
      toast.success("Views updated");
    },
    onError: () => toast.error("Failed to update views"),
  });

  function addMember(id: string) {
    setMembers.mutate([...memberIds, id]);
  }
  function removeMember(id: string) {
    setMembers.mutate(memberIds.filter((m) => m !== id));
  }
  function addView(id: string) {
    setViews.mutate([...viewIds, id]);
  }
  function removeView(id: string) {
    setViews.mutate(viewIds.filter((v) => v !== id));
  }

  return (
    <div className="flex flex-col gap-5">
      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-base font-semibold text-foreground">{group.name}</h2>
          {group.description && (
            <p className="text-xs text-muted-foreground mt-0.5">{group.description}</p>
          )}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <Button
            variant="ghost"
            size="sm"
            className="h-7 px-2 text-xs text-muted-foreground"
            onClick={onEdit}
          >
            Edit
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="h-7 px-2 text-xs text-destructive hover:text-destructive"
            onClick={onDelete}
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>

      {isLoading ? (
        <div className="space-y-4">
          <Skeleton className="h-20 w-full rounded-lg" />
          <Skeleton className="h-20 w-full rounded-lg" />
        </div>
      ) : (
        <>
          <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-4 space-y-3">
            <ChipList
              title="Members"
              items={agentItems}
              selected={memberIds}
              available={agentItems}
              onAdd={addMember}
              onRemove={removeMember}
              saving={setMembers.isPending}
            />
          </div>

          <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-4 space-y-3">
            <ChipList
              title="Views"
              items={viewItems}
              selected={viewIds}
              available={viewItems}
              onAdd={addView}
              onRemove={removeView}
              saving={setViews.isPending}
            />
          </div>
        </>
      )}
    </div>
  );
}

// ---- Group list item ----

function GroupListItem({
  group,
  selected,
  onClick,
}: {
  group: ViewGroupSummary;
  selected: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "w-full rounded-lg border px-3 py-2.5 text-left transition-colors",
        selected
          ? "border-primary/30 bg-primary/10"
          : "border-white/[0.06] bg-white/[0.02] hover:bg-white/[0.04] hover:border-white/10",
      )}
    >
      <p className={cn("text-sm font-medium", selected ? "text-primary" : "text-foreground")}>
        {group.name}
      </p>
      <p className="text-xs text-muted-foreground mt-0.5">
        {group.memberCount} member{group.memberCount !== 1 ? "s" : ""} ·{" "}
        {group.viewCount} view{group.viewCount !== 1 ? "s" : ""}
      </p>
    </button>
  );
}

// ---- Skeletons ----

function GroupListSkeleton() {
  return (
    <div className="space-y-2">
      {Array.from({ length: 3 }).map((_, i) => (
        <div key={i} className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 py-2.5">
          <Skeleton className="h-4 w-32 mb-1.5" />
          <Skeleton className="h-3 w-20" />
        </div>
      ))}
    </div>
  );
}

// ---- Page ----

export function ViewGroupsSettingsPage() {
  const qc = useQueryClient();
  const [selectedId, setSelectedId] = React.useState<string | null>(null);
  const [formGroup, setFormGroup] = React.useState<ViewGroupSummary | null | "new">(null);
  const [deletingGroup, setDeletingGroup] = React.useState<ViewGroupSummary | null>(null);

  const { data: groups, isLoading: groupsLoading } = useQuery({
    queryKey: ["view-groups"],
    queryFn: () => viewGroupApi.list(),
  });

  const { data: agents = [] } = useQuery({
    queryKey: ["agents"],
    queryFn: () => userApi.listAgents(),
  });

  const { data: views = [] } = useQuery({
    queryKey: ["views"],
    queryFn: () => viewApi.list(),
  });

  const selectedGroup = groups?.find((g) => g.id === selectedId) ?? null;

  function handleSaved(saved: ViewGroupSummary) {
    qc.invalidateQueries({ queryKey: ["view-groups"] });
    setSelectedId(saved.id);
    setFormGroup(null);
  }

  function handleDeleted() {
    qc.invalidateQueries({ queryKey: ["view-groups"] });
    setSelectedId(null);
    setDeletingGroup(null);
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/20 border border-primary/30">
            <Users className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h1 className="text-display-md font-semibold text-foreground leading-tight">
              View Groups
            </h1>
            {!groupsLoading && (
              <p className="text-xs text-muted-foreground">
                {groups?.length ?? 0} group{groups?.length !== 1 ? "s" : ""}
              </p>
            )}
          </div>
        </div>
        <Badge className="border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
          Admin only
        </Badge>
      </header>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[280px_1fr]">
        {/* Left: group list */}
        <div className="flex flex-col gap-3">
          <div className="flex items-center justify-between gap-2">
            <h2 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
              Groups
            </h2>
            <button
              type="button"
              onClick={() => setFormGroup("new")}
              className="inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs text-muted-foreground hover:text-foreground hover:bg-white/[0.06] transition-colors"
            >
              <Plus className="h-3 w-3" />
              New
            </button>
          </div>

          {groupsLoading ? (
            <GroupListSkeleton />
          ) : !groups || groups.length === 0 ? (
            <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] px-4 py-6 flex flex-col items-center gap-3 text-center">
              <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-primary/10 border border-primary/20">
                <Users className="h-4 w-4 text-primary/60" />
              </div>
              <div>
                <p className="text-sm font-medium text-foreground">No groups yet</p>
                <p className="text-xs text-muted-foreground mt-0.5">
                  Create a group to bundle views for agents.
                </p>
              </div>
              <Button variant="secondary" size="sm" onClick={() => setFormGroup("new")}>
                <Plus className="h-3.5 w-3.5" />
                Create group
              </Button>
            </div>
          ) : (
            <div className="space-y-1.5">
              {groups.map((group) => (
                <GroupListItem
                  key={group.id}
                  group={group}
                  selected={selectedId === group.id}
                  onClick={() => setSelectedId(group.id)}
                />
              ))}
            </div>
          )}
        </div>

        {/* Right: detail panel */}
        <div className="rounded-lg border border-white/[0.06] bg-white/[0.02] p-4">
          {selectedGroup ? (
            <GroupDetailPanel
              group={selectedGroup}
              agents={agents}
              views={views}
              onEdit={() => setFormGroup(selectedGroup)}
              onDelete={() => setDeletingGroup(selectedGroup)}
            />
          ) : (
            <div className="flex h-full min-h-[200px] flex-col items-center justify-center gap-3 text-center">
              <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-white/[0.04] border border-white/[0.06]">
                <Users className="h-4 w-4 text-muted-foreground/40" />
              </div>
              <p className="text-sm text-muted-foreground">
                Select a group to manage its members and views.
              </p>
            </div>
          )}
        </div>
      </div>

      {formGroup !== null && (
        <GroupFormDialog
          group={formGroup === "new" ? null : formGroup}
          onClose={() => setFormGroup(null)}
          onSaved={handleSaved}
        />
      )}

      {deletingGroup && (
        <DeleteDialog
          group={deletingGroup}
          onClose={() => setDeletingGroup(null)}
          onDeleted={handleDeleted}
        />
      )}
    </div>
  );
}
