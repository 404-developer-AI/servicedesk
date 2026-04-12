import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ticketApi } from "@/lib/ticket-api";
import { RichTextEditor } from "@/components/RichTextEditor";
import { cn } from "@/lib/utils";

type AddNoteFormProps = {
  ticketId: string;
  onSubmitted: () => void;
};

type TabType = "reply" | "note";

export function AddNoteForm({ ticketId, onSubmitted }: AddNoteFormProps) {
  const [tab, setTab] = React.useState<TabType>("reply");
  const [bodyHtml, setBodyHtml] = React.useState("");
  const [editorKey, setEditorKey] = React.useState(0);
  const queryClient = useQueryClient();

  const isInternal = tab === "note";

  const mutation = useMutation({
    mutationFn: () =>
      ticketApi.addEvent(ticketId, {
        eventType: isInternal ? "Note" : "Comment",
        bodyHtml: bodyHtml || undefined,
        isInternal,
      }),
    onSuccess: () => {
      toast.success(isInternal ? "Note added" : "Reply sent");
      setBodyHtml("");
      setEditorKey((k) => k + 1);
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      onSubmitted();
    },
    onError: () => {
      toast.error("Failed to submit — please try again");
    },
  });

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!bodyHtml.trim() || bodyHtml === "<p></p>") {
      toast.error("Please write something before submitting");
      return;
    }
    mutation.mutate();
  }

  return (
    <form
      onSubmit={handleSubmit}
      className={cn(
        "glass-card p-4",
        isInternal && "ring-1 ring-amber-500/30"
      )}
    >
      <div className="flex gap-1 mb-3">
        <button
          type="button"
          onClick={() => setTab("reply")}
          className={cn(
            "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
            tab === "reply"
              ? "bg-white/10 text-foreground"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
          )}
        >
          Reply
        </button>
        <button
          type="button"
          onClick={() => setTab("note")}
          className={cn(
            "px-3 py-1.5 rounded-md text-sm font-medium transition-colors",
            tab === "note"
              ? "bg-amber-500/15 text-amber-300 border border-amber-500/30"
              : "text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
          )}
        >
          Internal note
        </button>
      </div>

      <RichTextEditor
        key={editorKey}
        onChange={setBodyHtml}
        placeholder={
          isInternal
            ? "Add an internal note (not visible to customers)…"
            : "Write a reply to the customer…"
        }
        minHeight="120px"
      />

      <div className="mt-3 flex justify-end">
        <button
          type="submit"
          disabled={mutation.isPending}
          className={cn(
            "px-4 py-2 rounded-md text-sm font-medium transition-colors",
            isInternal
              ? "bg-amber-500/20 text-amber-300 border border-amber-500/30 hover:bg-amber-500/30"
              : "bg-primary/20 text-primary border border-primary/30 hover:bg-primary/30",
            mutation.isPending && "opacity-50 cursor-not-allowed"
          )}
        >
          {mutation.isPending
            ? "Submitting…"
            : isInternal
            ? "Add note"
            : "Add reply"}
        </button>
      </div>
    </form>
  );
}
