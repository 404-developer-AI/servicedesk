import * as React from "react";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import type { TabType } from "./composerTypes";

export function usePendingMailAction({
  ticketId,
  internalOnly,
  setExpanded,
  setTab,
}: {
  ticketId: string;
  internalOnly: boolean;
  setExpanded: React.Dispatch<React.SetStateAction<boolean>>;
  setTab: React.Dispatch<React.SetStateAction<TabType>>;
}) {
  const pendingAction = useWorkspaceStore((s) =>
    s.pendingMailAction && s.pendingMailAction.ticketId === ticketId
      ? s.pendingMailAction
      : null,
  );

  // When the agent clicks Reply / Reply-all / Forward on a MailReceived event,
  // the event card sets pendingMailAction — we react by expanding the form and
  // switching to the mail tab. <SendMailForm> picks up the same intent via
  // props and applies it to its own state.
  React.useEffect(() => {
    if (pendingAction && !internalOnly) {
      setExpanded(true);
      setTab("mail");
    }
  }, [pendingAction?.id, pendingAction, internalOnly, setExpanded, setTab]);

  return pendingAction;
}
