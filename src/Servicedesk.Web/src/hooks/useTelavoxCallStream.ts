import * as React from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import { useCurrentRole } from "@/hooks/useCurrentRole";
import { useIncomingCallStore, type IncomingCallEvent } from "@/stores/useIncomingCallStore";

let connection: HubConnection | null = null;

function getConnection(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl("/hubs/telavox-calls")
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
  }
  return connection;
}

/// Mounts the /hubs/telavox-calls connection for agents/admins. Each
/// "TelavoxCall" push lands in the global incoming-call store so the
/// shell-level popup picks it up. Customers and non-mapped agents see no
/// activity — the server only sends events to the per-user group derived
/// from the authenticated NameIdentifier claim.
export function useTelavoxCallStream() {
  const role = useCurrentRole();
  const push = useIncomingCallStore((s) => s.push);

  React.useEffect(() => {
    // Customers never receive call events. The hub itself enforces this
    // via the RequireAgent policy; this check just avoids opening a doomed
    // WebSocket for them.
    if (role !== "Agent" && role !== "Admin") return;

    const hub = getConnection();

    const handler = (payload: IncomingCallEvent) => {
      // Shape-validate at the boundary — a malformed push from a future
      // server version must not blow up the popup. Drop silently rather
      // than throw, because the worker-side audit row is the canonical
      // signal for "something pushed".
      if (
        typeof payload !== "object" ||
        payload === null ||
        typeof payload.callId !== "string" ||
        typeof payload.state !== "string"
      ) {
        return;
      }
      push(payload);
    };

    hub.on("TelavoxCall", handler);

    async function start() {
      if (hub.state === HubConnectionState.Disconnected) {
        try {
          await hub.start();
        } catch {
          // Silent — a logged-out session or a brief network outage should
          // not surface a red toast. The autoReconnect policy handles the
          // recovery path. Worst case: the next call event arrives via
          // the integration_audit log instead of a live popup.
        }
      }
    }
    void start();

    return () => {
      hub.off("TelavoxCall", handler);
    };
  }, [role, push]);
}
