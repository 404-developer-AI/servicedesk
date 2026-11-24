import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ticketApi } from "@/lib/ticket-api";
import { splitMentionIds } from "@/components/editorDocIds";
import type { useAttachmentUploads } from "../../hooks/useAttachmentUploads";
import type { TabType } from "./composerTypes";

export function useComposerSubmit({
  ticketId,
  tab,
  isCall,
  isInternal,
  bodyHtml,
  setBodyHtml,
  mentionedUserIds,
  setMentionedUserIds,
  setEditorKey,
  setExpanded,
  attachments,
  clearDraft,
  onSubmitted,
}: {
  ticketId: string;
  tab: TabType;
  isCall: boolean;
  isInternal: boolean;
  bodyHtml: string;
  setBodyHtml: React.Dispatch<React.SetStateAction<string>>;
  mentionedUserIds: string[];
  setMentionedUserIds: React.Dispatch<React.SetStateAction<string[]>>;
  setEditorKey: React.Dispatch<React.SetStateAction<number>>;
  setExpanded: React.Dispatch<React.SetStateAction<boolean>>;
  attachments: ReturnType<typeof useAttachmentUploads>;
  clearDraft: (forTab: TabType) => void;
  onSubmitted: () => void;
}) {
  const queryClient = useQueryClient();

  const mutation = useMutation({
    mutationFn: () => {
      const { userIds, mailboxIds, groupIds } = splitMentionIds(mentionedUserIds);
      return ticketApi.addEvent(ticketId, {
        eventType: isCall ? "Call" : isInternal ? "Note" : "Comment",
        bodyHtml: bodyHtml || undefined,
        isInternal,
        attachmentIds: attachments.readyAttachmentIds,
        mentionedUserIds: userIds.length > 0 ? userIds : undefined,
        mentionedMailboxIds: mailboxIds.length > 0 ? mailboxIds : undefined,
        mentionedGroupIds: groupIds.length > 0 ? groupIds : undefined,
      });
    },
    onSuccess: () => {
      toast.success(isCall ? "Call logged" : isInternal ? "Note added" : "Reply sent");
      clearDraft(tab);
      // A logged call can clear the ticket's Call-back flag server-side
      // (Tickets.CallbackClearOnCall) — refresh the list so the float moves.
      if (isCall) queryClient.invalidateQueries({ queryKey: ["tickets"] });
      setBodyHtml("");
      setMentionedUserIds([]);
      attachments.reset();
      setEditorKey((k) => k + 1);
      setExpanded(false);
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      onSubmitted();
    },
    onError: () => {
      toast.error("Failed to submit — please try again");
    },
  });

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    const hasBody = bodyHtml.trim() && bodyHtml !== "<p></p>";
    const hasAttachments = attachments.readyAttachmentIds.length > 0;
    if (!hasBody && !hasAttachments) {
      toast.error("Please write something or attach a file before submitting");
      return;
    }
    if (attachments.hasPending) {
      toast.error("Wait for attachment uploads to finish");
      return;
    }
    mutation.mutate();
  }

  return { mutation, handleSubmit };
}
