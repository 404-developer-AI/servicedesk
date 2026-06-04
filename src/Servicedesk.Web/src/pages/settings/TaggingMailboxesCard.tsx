import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  Mailbox,
  Plus,
  Pencil,
  Trash2,
  Loader2,
  AtSign,
} from "lucide-react";
import {
  taggingMailboxApi,
  type TaggingMailbox,
  type TaggingMailboxInput,
} from "@/lib/ticket-api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

// Light client-side guard; the server is the source of truth (FluentValidation-
// style manual checks + a unique-email constraint).
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/// Admin-only management of login-less @@-mention targets. Rendered as the
/// first card on Settings → Users. Mentioning one of these mailboxes in a
/// note / reply / outbound mail sends a notification e-mail to its address;
/// it has no user row, role or tickets.
export function TaggingMailboxesCard() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({
    queryKey: ["admin", "tagging-mailboxes"],
    queryFn: () => taggingMailboxApi.list(),
  });

  const [editorOpen, setEditorOpen] = useState(false);
  const [editing, setEditing] = useState<TaggingMailbox | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<TaggingMailbox | null>(null);

  function invalidate() {
    qc.invalidateQueries({ queryKey: ["admin", "tagging-mailboxes"] });
  }

  const removeMutation = useMutation({
    mutationFn: (id: string) => taggingMailboxApi.remove(id),
    onSuccess: () => {
      toast.success("Tagging mailbox deleted");
      setDeleteTarget(null);
      invalidate();
    },
    onError: (e: unknown) =>
      toast.error(e instanceof Error ? e.message : "Could not delete mailbox"),
  });

  function openAdd() {
    setEditing(null);
    setEditorOpen(true);
  }
  function openEdit(m: TaggingMailbox) {
    setEditing(m);
    setEditorOpen(true);
  }

  const list = data ?? [];

  return (
    <section className="rounded-xl border border-glass bg-glass p-5">
      <header className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg border border-emerald-500/30 bg-emerald-500/15">
            <Mailbox className="h-4 w-4 text-emerald-500 dark:text-emerald-300" />
          </div>
          <div>
            <h2 className="text-sm font-semibold leading-tight text-foreground">
              Mailboxes only usable for tagging
            </h2>
            <p className="mt-0.5 text-[11px] text-muted-foreground">
              Login-less @@-mention targets. Tag one in a note, reply or mail and
              it receives a notification e-mail — no account, role or tickets.
            </p>
          </div>
        </div>
        <Button onClick={openAdd} size="sm" variant="secondary" className="gap-1.5 shrink-0">
          <Plus className="h-4 w-4" />
          Add mailbox
        </Button>
      </header>

      <div className="mt-4">
        {isLoading ? (
          <div className="space-y-2">
            {Array.from({ length: 2 }).map((_, i) => (
              <Skeleton key={i} className="h-[52px] w-full rounded-lg" />
            ))}
          </div>
        ) : list.length === 0 ? (
          <div className="rounded-lg border border-dashed border-glass-strong bg-glass px-6 py-6 text-center text-xs text-muted-foreground">
            No tagging mailboxes yet. Add one to make it available in the @@ picker.
          </div>
        ) : (
          <div className="space-y-2">
            {list.map((m) => (
              <div
                key={m.id}
                className="flex items-center gap-3 rounded-lg border border-glass bg-glass-hover/40 px-3 py-2.5"
              >
                <AtSign className="h-4 w-4 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate text-sm font-medium text-foreground">
                      {m.name}
                    </span>
                    {!m.isActive && (
                      <Badge className="border border-glass bg-glass text-[10px] font-normal text-muted-foreground">
                        Inactive
                      </Badge>
                    )}
                  </div>
                  <span className="truncate text-xs text-muted-foreground">{m.email}</span>
                </div>
                <Button
                  size="icon"
                  variant="ghost"
                  className="h-8 w-8 shrink-0"
                  onClick={() => openEdit(m)}
                  title="Edit"
                >
                  <Pencil className="h-4 w-4" />
                </Button>
                <Button
                  size="icon"
                  variant="ghost"
                  className="h-8 w-8 shrink-0 text-destructive hover:text-destructive"
                  onClick={() => setDeleteTarget(m)}
                  title="Delete"
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </div>
            ))}
          </div>
        )}
      </div>

      <MailboxEditorDialog
        open={editorOpen}
        onOpenChange={setEditorOpen}
        editing={editing}
        onSaved={invalidate}
      />

      <Dialog open={!!deleteTarget} onOpenChange={(o) => !o && setDeleteTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete tagging mailbox</DialogTitle>
            <DialogDescription>
              Remove <span className="font-medium text-foreground">{deleteTarget?.name}</span>{" "}
              ({deleteTarget?.email})? It will no longer appear in the @@ picker.
              Past mentions stay in the ticket history.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="ghost" onClick={() => setDeleteTarget(null)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={() => deleteTarget && removeMutation.mutate(deleteTarget.id)}
              disabled={removeMutation.isPending}
              className="gap-1.5"
            >
              {removeMutation.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </section>
  );
}

function MailboxEditorDialog({
  open,
  onOpenChange,
  editing,
  onSaved,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  editing: TaggingMailbox | null;
  onSaved: () => void;
}) {
  const isEdit = !!editing;
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [isActive, setIsActive] = useState(true);

  // Reset the form whenever the dialog (re)opens for a different target.
  // Keyed off `open` + the editing id so re-opening Add after an Edit clears.
  const [syncKey, setSyncKey] = useState<string>("");
  const wantKey = `${open}:${editing?.id ?? "new"}`;
  if (open && syncKey !== wantKey) {
    setSyncKey(wantKey);
    setName(editing?.name ?? "");
    setEmail(editing?.email ?? "");
    setIsActive(editing?.isActive ?? true);
  }

  const mutation = useMutation({
    mutationFn: (input: TaggingMailboxInput) =>
      isEdit
        ? taggingMailboxApi.update(editing!.id, input)
        : taggingMailboxApi.create(input),
    onSuccess: () => {
      toast.success(isEdit ? "Tagging mailbox updated" : "Tagging mailbox added");
      onOpenChange(false);
      onSaved();
    },
    onError: (e: unknown) =>
      toast.error(e instanceof Error ? e.message : "Could not save mailbox"),
  });

  function submit() {
    const trimmedName = name.trim();
    const trimmedEmail = email.trim();
    if (!trimmedName) {
      toast.error("Name is required.");
      return;
    }
    if (!EMAIL_RE.test(trimmedEmail)) {
      toast.error("Enter a valid e-mail address.");
      return;
    }
    mutation.mutate({ name: trimmedName, email: trimmedEmail, isActive });
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{isEdit ? "Edit tagging mailbox" : "Add tagging mailbox"}</DialogTitle>
          <DialogDescription>
            A display name and an e-mail address. The address receives a
            notification mail whenever the mailbox is @@-tagged.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4 py-1">
          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">Name</label>
            <Input
              autoFocus
              placeholder="e.g. Accounting"
              value={name}
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && submit()}
            />
          </div>
          <div className="space-y-1.5">
            <label className="text-xs font-medium text-muted-foreground">E-mail address</label>
            <Input
              type="email"
              placeholder="accounting@example.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && submit()}
            />
          </div>
          <label className="flex items-center gap-2 text-sm text-foreground">
            <input
              type="checkbox"
              checked={isActive}
              onChange={(e) => setIsActive(e.target.checked)}
              className="h-4 w-4 rounded border-glass-strong accent-primary"
            />
            Active — show in the @@ picker
          </label>
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={submit} disabled={mutation.isPending} className="gap-1.5">
            {mutation.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
            {isEdit ? "Save" : "Add mailbox"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
