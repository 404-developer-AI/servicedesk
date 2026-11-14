import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Pencil, Plus, Trash2, Users, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { AgentPicker } from "@/components/AgentPicker";
import { useConfirm } from "@/components/ConfirmDialog";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { errorMessage } from "@/lib/http";
import { userApi } from "@/lib/ticket-api";
import { tagGroupApi, type TagGroup, type TagGroupInput } from "@/lib/group-tags-api";

const KEY = ["admin", "tag-groups"] as const;

/// v0.1.54 — admin management of group tags (Settings → Users). Tagging a
/// group with @@ gives all its members one shared tag in their Group tags
/// block — never a mail.
export function TagGroupsCard() {
  const qc = useQueryClient();
  const confirm = useConfirm();
  const { data, isLoading } = useQuery({ queryKey: KEY, queryFn: () => tagGroupApi.list() });
  const [editorOpen, setEditorOpen] = useState(false);
  const [editing, setEditing] = useState<TagGroup | null>(null);

  const invalidate = () => void qc.invalidateQueries({ queryKey: KEY });
  const remove = useMutation({
    mutationFn: (id: string) => tagGroupApi.remove(id),
    onSuccess: () => {
      toast.success("Group deleted");
      invalidate();
    },
    onError: (e) => toast.error(errorMessage(e, "Could not delete the group")),
  });

  async function askDelete(g: TagGroup) {
    const ok = await confirm({
      title: "Delete group",
      description: `Delete “${g.name}”? It disappears from the @@ picker, and its tags (open and history) are removed for every member.`,
      confirmLabel: "Delete",
      tone: "destructive",
    });
    if (ok) remove.mutate(g.id);
  }

  const list = data ?? [];
  return (
    <section className="rounded-xl border border-glass bg-glass p-5">
      <header className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg border border-primary/25 bg-primary/10">
            <Users className="h-4 w-4 text-primary" />
          </div>
          <div>
            <h2 className="text-sm font-semibold leading-tight text-foreground">Group tags</h2>
            <p className="mt-0.5 text-[11px] text-muted-foreground">
              Groups of agents you can tag with @@ like a person. Every member sees one shared tag in their Group tags
              block until one of them acknowledges it. Never a mail.
            </p>
          </div>
        </div>
        <Button onClick={() => { setEditing(null); setEditorOpen(true); }} size="sm" variant="secondary" className="shrink-0 gap-1.5">
          <Plus className="h-4 w-4" />
          Add group
        </Button>
      </header>

      <div className="mt-4">
        {isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-[52px] w-full rounded-lg" />
            <Skeleton className="h-[52px] w-full rounded-lg" />
          </div>
        ) : list.length === 0 ? (
          <div className="rounded-lg border border-dashed border-glass-strong bg-glass px-6 py-6 text-center text-xs text-muted-foreground">
            No groups yet. Add one, e.g. “Back office”, and pick its members.
          </div>
        ) : (
          <div className="space-y-2">
            {list.map((g) => (
              <div key={g.id} className="flex items-center gap-3 rounded-lg border border-glass bg-glass-hover/40 px-3 py-2.5">
                <Users className="h-4 w-4 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate text-sm font-medium text-foreground">{g.name}</span>
                    {!g.isActive && (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">Inactive</Badge>
                    )}
                  </div>
                  <span className="block truncate text-xs text-muted-foreground">
                    {g.members.length === 0
                      ? "No members"
                      : `${g.members.length} member${g.members.length === 1 ? "" : "s"} · ${g.members.map((m) => m.email).join(", ")}`}
                  </span>
                </div>
                <Button size="icon" variant="ghost" className="h-8 w-8 shrink-0" title="Edit"
                  onClick={() => { setEditing(g); setEditorOpen(true); }}>
                  <Pencil className="h-4 w-4" />
                </Button>
                <Button size="icon" variant="ghost" className="h-8 w-8 shrink-0 text-destructive hover:text-destructive"
                  title="Delete" onClick={() => void askDelete(g)}>
                  <Trash2 className="h-4 w-4" />
                </Button>
              </div>
            ))}
          </div>
        )}
      </div>

      <GroupEditorDialog open={editorOpen} onOpenChange={setEditorOpen} editing={editing} onSaved={invalidate} />
    </section>
  );
}

function GroupEditorDialog({
  open,
  onOpenChange,
  editing,
  onSaved,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  editing: TagGroup | null;
  onSaved: () => void;
}) {
  const isEdit = !!editing;
  const [name, setName] = useState("");
  const [isActive, setIsActive] = useState(true);
  const [memberIds, setMemberIds] = useState<string[]>([]);
  const agentsQ = useQuery({ queryKey: ["agents"], queryFn: userApi.listAgents, staleTime: 5 * 60_000 });

  // Reset the form whenever the dialog (re)opens for another target.
  const [syncKey, setSyncKey] = useState("");
  const wantKey = `${open}:${editing?.id ?? "new"}`;
  if (open && syncKey !== wantKey) {
    setSyncKey(wantKey);
    setName(editing?.name ?? "");
    setIsActive(editing?.isActive ?? true);
    setMemberIds(editing?.members.map((m) => m.userId) ?? []);
  }

  const emailOf = (id: string) =>
    agentsQ.data?.find((a) => a.id === id)?.email
    ?? editing?.members.find((m) => m.userId === id)?.email
    ?? "Unknown agent";

  const save = useMutation({
    mutationFn: (input: TagGroupInput) => (isEdit ? tagGroupApi.update(editing!.id, input) : tagGroupApi.create(input)),
    onSuccess: () => {
      toast.success(isEdit ? "Group updated" : "Group added");
      onOpenChange(false);
      onSaved();
    },
    onError: (e) => toast.error(errorMessage(e, "Could not save the group")),
  });

  function submit() {
    const trimmed = name.trim();
    if (!trimmed) {
      toast.error("Name is required.");
      return;
    }
    save.mutate({ name: trimmed, isActive, memberIds });
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{isEdit ? "Edit group" : "Add group"}</DialogTitle>
          <DialogDescription>
            Members see the group's tags in their Group tags block. An agent can be in several groups.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4 py-1">
          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">Name</label>
            <Input autoFocus maxLength={200} placeholder="e.g. Back office" value={name}
              onChange={(e) => setName(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">Members</label>
            {memberIds.length > 0 && (
              <div className="flex flex-wrap gap-1.5">
                {memberIds.map((id) => (
                  <span key={id} className="inline-flex items-center gap-1 rounded-md border border-glass-strong bg-glass px-2 py-0.5 text-xs">
                    {emailOf(id)}
                    <button type="button" aria-label={`Remove ${emailOf(id)}`}
                      onClick={() => setMemberIds((ids) => ids.filter((x) => x !== id))}
                      className="text-muted-foreground hover:text-foreground">
                      <X className="h-3 w-3" />
                    </button>
                  </span>
                ))}
              </div>
            )}
            <AgentPicker value={null} placeholder="Add a member…"
              onChange={(id) => { if (id && !memberIds.includes(id)) setMemberIds((ids) => [...ids, id]); }} />
          </div>
          <label className="flex items-center gap-2 text-sm text-foreground">
            <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)}
              className="h-4 w-4 rounded border-glass-strong accent-primary" />
            Active — show in the @@ picker and in the members' Group tags block
          </label>
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button onClick={submit} disabled={save.isPending} className="gap-1.5">
            {save.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
            {isEdit ? "Save" : "Add group"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
