import * as React from "react";
import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { toast } from "sonner";
import { ensureNotificationConnection } from "@/hooks/useNotificationSignalR";
import { GROUP_TAGS_KEY } from "@/lib/group-tags-api";

/// Matches GroupTagPush on the server.
type GroupTagPush = {
  id: string;
  groupId: string;
  groupName: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  eventId: number;
  sourceUserEmail: string | null;
  previewText: string;
};

/// v0.1.54 — group tags realtime on the shared per-user notifications hub.
/// `GroupTagReceived` → refresh + toast; `GroupTagsChanged` (a colleague
/// acknowledged one) → refresh, so the tag disappears for every member.
export function useGroupTagsSignalR(toastDurationMs: number) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const navigateRef = React.useRef(navigate);
  navigateRef.current = navigate;
  const durationRef = React.useRef(toastDurationMs);
  durationRef.current = toastDurationMs;

  React.useEffect(() => {
    const hub = ensureNotificationConnection();
    const refresh = () => void queryClient.invalidateQueries({ queryKey: GROUP_TAGS_KEY });
    const received = (p: GroupTagPush) => {
      refresh();
      const who = p.sourceUserEmail?.split("@")[0] ?? "agent";
      toast.message(`@${who} tagged ${p.groupName} in #${p.ticketNumber}`, {
        description: p.previewText || p.ticketSubject,
        duration: durationRef.current,
        action: {
          label: "View",
          onClick: () => void navigateRef.current({
            to: "/tickets/$ticketId",
            params: { ticketId: p.ticketId },
            hash: `event-${p.eventId}`,
          }),
        },
      });
    };
    hub.on("GroupTagReceived", received);
    hub.on("GroupTagsChanged", refresh);
    if (hub.state === HubConnectionState.Disconnected) hub.start().catch(() => {});
    return () => {
      hub.off("GroupTagReceived", received);
      hub.off("GroupTagsChanged", refresh);
    };
  }, [queryClient]);
}
