import { create } from "zustand";

/// Server-pushed payload for one Telavox call event. Mirrors the
/// TelavoxCallEventPayload record on the .NET side. Persisted in a tiny
/// global store so the popup component can mount once at shell level and
/// react to new events regardless of which page the agent is on.
export type IncomingCallEvent = {
  callId: string;
  state: string;
  fromNumber: string | null;
  toNumber: string | null;
  startUtc: string | null;
  occurredUtc: string;
};

type IncomingCallState = {
  current: IncomingCallEvent | null;
  /// Replace the current event. A new callId fully supersedes the old
  /// one; same-callId pushes update the state (RINGING → ANSWERED) but
  /// keep the popup open with the same baseline so the agent doesn't see
  /// a flicker on the state-edge.
  push: (event: IncomingCallEvent) => void;
  dismiss: (callId?: string) => void;
};

export const useIncomingCallStore = create<IncomingCallState>((set) => ({
  current: null,
  push: (event) =>
    set(() => {
      // Always accept the freshest event — the server-side state-machine
      // already debounced same-state ticks, so anything that reaches us
      // is a meaningful change.
      return { current: event };
    }),
  dismiss: (callId) =>
    set((s) => {
      // Optional callId guard so a delayed "no-call" tick can't accidentally
      // clear a popup the user has already replaced with a newer call.
      if (callId && s.current?.callId !== callId) return s;
      return { current: null };
    }),
}));
