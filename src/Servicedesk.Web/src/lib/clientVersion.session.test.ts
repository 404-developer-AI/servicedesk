import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  installClientVersionFetch,
  SESSION_EXPIRED_EVENT,
  CLIENT_VERSION_OUTDATED_EVENT,
} from "@/lib/clientVersion";

// The global fetch wrapper turns a mid-session 401 into a window event, but
// only for authenticated /api data calls — never the sign-in flow itself.
describe("installClientVersionFetch — 401 → session-expired event", () => {
  const realFetch = window.fetch;
  let events: Array<{ type: string; path?: string }>;

  const onExpired = (e: Event) => {
    events.push({ type: SESSION_EXPIRED_EVENT, path: (e as CustomEvent<{ path?: string }>).detail?.path });
  };
  const onOutdated = () => {
    events.push({ type: CLIENT_VERSION_OUTDATED_EVENT });
  };

  beforeEach(() => {
    events = [];
    window.addEventListener(SESSION_EXPIRED_EVENT, onExpired);
    window.addEventListener(CLIENT_VERSION_OUTDATED_EVENT, onOutdated);
  });

  afterEach(() => {
    window.removeEventListener(SESSION_EXPIRED_EVENT, onExpired);
    window.removeEventListener(CLIENT_VERSION_OUTDATED_EVENT, onOutdated);
    window.fetch = realFetch;
    vi.restoreAllMocks();
  });

  function armFetch(status: number) {
    window.fetch = vi.fn(async () => new Response(null, { status })) as typeof window.fetch;
    installClientVersionFetch();
  }

  it("dispatches session-expired for a 401 on an authenticated data call", async () => {
    armFetch(401);
    await window.fetch("/api/tickets/71bc0580-d091-4849-ab60-481683d69ee5/mail", { method: "POST" });
    expect(events).toEqual([{ type: SESSION_EXPIRED_EVENT, path: "/api/tickets/71bc0580-d091-4849-ab60-481683d69ee5/mail" }]);
  });

  it("does NOT dispatch for a 401 on the staff auth flow", async () => {
    armFetch(401);
    await window.fetch("/api/auth/me");
    await window.fetch("/api/auth/login", { method: "POST" });
    expect(events).toEqual([]);
  });

  it("does NOT dispatch for a 401 on the portal auth flow", async () => {
    armFetch(401);
    await window.fetch("/api/portal/auth/me");
    expect(events).toEqual([]);
  });

  it("dispatches for a 401 on a portal DATA call (portal path carried through)", async () => {
    armFetch(401);
    await window.fetch("/api/portal/tickets");
    expect(events).toEqual([{ type: SESSION_EXPIRED_EVENT, path: "/api/portal/tickets" }]);
  });

  it("does not dispatch on a 2xx", async () => {
    armFetch(200);
    await window.fetch("/api/tickets");
    expect(events).toEqual([]);
  });

  it("still dispatches the version-outdated event on a 426", async () => {
    armFetch(426);
    await window.fetch("/api/tickets", { method: "PUT" });
    expect(events).toEqual([{ type: CLIENT_VERSION_OUTDATED_EVENT }]);
  });
});
