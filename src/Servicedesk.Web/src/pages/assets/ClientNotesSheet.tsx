import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { MessageSquare, Pencil, Send, Trash2, X } from "lucide-react";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  apiErrorMessage,
  remoteDesktopApi,
  type RemoteDesktopClient,
  type RemoteDesktopClientNote,
} from "@/lib/api";
import { useAuth } from "@/auth/authStore";
import { toServerLocal, useServerTime } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";

export type NotesTarget = Pick<
  RemoteDesktopClient,
  "trmmClientId" | "displayName" | "code" | "companyName"
>;

/// Per-client notes thread for Assets → Remote Desktop (v0.1.10). A
/// running log rather than one editable field, so two colleagues never
/// overwrite each other: every note carries its author + server-side
/// timestamp. Edit/delete is author-or-admin (enforced server-side; the
/// buttons only mirror that rule).
export function ClientNotesSheet({
  target,
  onClose,
}: {
  target: NotesTarget | null;
  onClose: () => void;
}) {
  const open = target !== null;
  const qc = useQueryClient();
  const { user } = useAuth();
  const myId = user?.id ?? "";
  const isAdmin = user?.role === "Admin";
  const { time: serverTime } = useServerTime();
  const offsetMinutes = serverTime?.offsetMinutes ?? 0;

  const clientId = target?.trmmClientId ?? null;
  const notes = useQuery({
    queryKey: ["assets", "remote-desktop", "notes", clientId] as const,
    queryFn: () => remoteDesktopApi.listNotes(clientId as number),
    enabled: open && clientId !== null,
  });

  const [draft, setDraft] = React.useState("");
  const [editingId, setEditingId] = React.useState<string | null>(null);
  const [editDraft, setEditDraft] = React.useState("");
  React.useEffect(() => {
    if (!open) {
      setDraft("");
      setEditingId(null);
      setEditDraft("");
    }
  }, [open]);

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["assets", "remote-desktop"] });
  };

  const add = useMutation({
    mutationFn: () => remoteDesktopApi.addNote(clientId as number, draft),
    onSuccess: () => {
      setDraft("");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not save the note"),
  });

  const update = useMutation({
    mutationFn: ({ id, body }: { id: string; body: string }) =>
      remoteDesktopApi.updateNote(id, body),
    onSuccess: () => {
      setEditingId(null);
      setEditDraft("");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not update the note"),
  });

  const remove = useMutation({
    mutationFn: (id: string) => remoteDesktopApi.deleteNote(id),
    onSuccess: () => {
      toast.success("Note deleted");
      invalidate();
    },
    onError: (err) => toast.error(apiErrorMessage(err) ?? "Could not delete the note"),
  });

  const canSend = draft.trim().length > 0 && !add.isPending;
  const submit = () => {
    if (canSend) add.mutate();
  };

  const canManage = (n: RemoteDesktopClientNote) => isAdmin || (n.authorId !== null && n.authorId === myId);

  return (
    <Sheet open={open} onOpenChange={(o) => !o && onClose()}>
      <SheetContent className="flex w-full max-w-md flex-col sm:max-w-lg">
        <SheetHeader>
          <SheetTitle className="flex items-center gap-2">
            <MessageSquare className="h-4 w-4 text-primary" />
            <span className="truncate">
              {target?.code && (
                <span className="mr-2 rounded-md border border-primary/30 bg-primary/10 px-1.5 py-0.5 font-mono text-xs text-primary">
                  {target.code}
                </span>
              )}
              {target?.displayName ?? "Client"}
            </span>
          </SheetTitle>
          <p className="text-xs text-muted-foreground">
            Notes about this client's Remote Desktop servers. Everyone with access to
            Assets can read them; you can edit or delete your own.
          </p>
        </SheetHeader>

        <div className="mt-4 flex min-h-0 flex-1 flex-col gap-3">
          <div className="min-h-0 flex-1 space-y-2 overflow-y-auto pr-1">
            {notes.isLoading ? (
              <Skeleton className="h-24 w-full" />
            ) : (notes.data?.items.length ?? 0) === 0 ? (
              <p className="rounded-md border border-dashed border-glass-strong bg-glass-strong/50 p-4 text-center text-xs text-muted-foreground">
                No notes yet. Add the first one below.
              </p>
            ) : (
              notes.data!.items.map((n) => (
                <article
                  key={n.id}
                  className="rounded-lg border border-glass bg-glass p-3 text-sm"
                >
                  <div className="mb-1 flex items-center justify-between gap-2">
                    <div className="min-w-0 truncate text-xs text-muted-foreground">
                      <span className="font-medium text-foreground">
                        {n.authorEmail ?? "Former user"}
                      </span>
                      <span className="mx-1.5">·</span>
                      <span title={toServerLocal(n.createdUtc, offsetMinutes, true)}>
                        {toServerLocal(n.createdUtc, offsetMinutes)}
                      </span>
                      {n.edited && (
                        <span
                          className="ml-1.5 italic"
                          title={`Edited ${toServerLocal(n.updatedUtc, offsetMinutes)}`}
                        >
                          (edited)
                        </span>
                      )}
                    </div>
                    {canManage(n) && editingId !== n.id && (
                      <div className="flex shrink-0 items-center gap-0.5">
                        <button
                          type="button"
                          className="rounded p-1 text-muted-foreground transition-colors hover:bg-glass-hover hover:text-foreground"
                          title="Edit note"
                          onClick={() => {
                            setEditingId(n.id);
                            setEditDraft(n.body);
                          }}
                        >
                          <Pencil className="h-3.5 w-3.5" />
                        </button>
                        <button
                          type="button"
                          className="rounded p-1 text-muted-foreground transition-colors hover:bg-glass-hover hover:text-destructive"
                          title="Delete note"
                          disabled={remove.isPending}
                          onClick={() => remove.mutate(n.id)}
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </button>
                      </div>
                    )}
                  </div>
                  {editingId === n.id ? (
                    <div className="space-y-2">
                      <textarea
                        value={editDraft}
                        onChange={(e) => setEditDraft(e.target.value)}
                        rows={3}
                        className="w-full resize-y rounded-md border border-glass bg-glass-strong px-2 py-1.5 text-sm text-foreground outline-none focus:border-primary/50"
                      />
                      <div className="flex justify-end gap-1">
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => {
                            setEditingId(null);
                            setEditDraft("");
                          }}
                        >
                          <X className="mr-1 h-3 w-3" /> Cancel
                        </Button>
                        <Button
                          size="sm"
                          disabled={editDraft.trim().length === 0 || update.isPending}
                          onClick={() => update.mutate({ id: n.id, body: editDraft })}
                        >
                          Save
                        </Button>
                      </div>
                    </div>
                  ) : (
                    <p className="whitespace-pre-wrap break-words text-foreground">{n.body}</p>
                  )}
                </article>
              ))
            )}
          </div>

          <div className="shrink-0 space-y-2 border-t border-glass pt-3">
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
                  e.preventDefault();
                  submit();
                }
              }}
              rows={3}
              placeholder="Add a note for this client…"
              className={cn(
                "w-full resize-y rounded-md border border-glass bg-glass-strong px-2 py-1.5 text-sm text-foreground outline-none placeholder:text-muted-foreground/60 focus:border-primary/50",
              )}
            />
            <div className="flex items-center justify-between">
              <span className="text-[11px] text-muted-foreground">Ctrl+Enter to send</span>
              <Button size="sm" onClick={submit} disabled={!canSend}>
                <Send className="mr-1.5 h-3 w-3" /> Add note
              </Button>
            </div>
          </div>
        </div>
      </SheetContent>
    </Sheet>
  );
}
