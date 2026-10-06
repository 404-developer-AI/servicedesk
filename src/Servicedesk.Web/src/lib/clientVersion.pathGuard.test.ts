import { afterEach, describe, expect, it, vi } from "vitest";
import { hasDotSegment, installClientVersionFetch } from "@/lib/clientVersion";
import { htmlHasText } from "@/portal/PortalComposer";

// The global fetch wrapper refuses /api request strings whose path holds a
// dot-segment, so an unencoded id can never re-route a call to another endpoint.
describe("hasDotSegment", () => {
  it.each([
    "/api/tickets/../settings/x",
    "/api/tickets/..",
    "/api/tickets/./checklists",
    "/api/tickets/%2e%2e/settings",
    "/api/tickets/.%2E/settings",
    "/api/tickets/..\\settings",
  ])("flags %s", (raw) => {
    expect(hasDotSegment(raw)).toBe(true);
  });

  it.each([
    "/api/tickets/71bc0580-d091-4849-ab60-481683d69ee5/checklists",
    "/api/kb/articles/v1.2-release-notes",
    "/api/tickets/123/attachments/file..name",
    "/api/search?q=../x",
    "/api/tickets/%2e%2ehidden",
  ])("allows %s", (raw) => {
    expect(hasDotSegment(raw)).toBe(false);
  });
});

describe("installClientVersionFetch — dot-segment guard", () => {
  const realFetch = window.fetch;

  afterEach(() => {
    window.fetch = realFetch;
    vi.restoreAllMocks();
  });

  it("rejects a traversal path without sending it", async () => {
    const inner = vi.fn(async () => new Response(null, { status: 200 }));
    window.fetch = inner as typeof window.fetch;
    installClientVersionFetch();

    await expect(window.fetch("/api/tickets/../settings/x", { method: "DELETE" })).rejects.toThrow(TypeError);
    // Also when the traversal climbs out of /api entirely.
    await expect(window.fetch("/api/tickets/../../logout", { method: "POST" })).rejects.toThrow(TypeError);
    expect(inner).not.toHaveBeenCalled();
  });

  it("passes a normal API path through", async () => {
    const inner = vi.fn(async () => new Response(null, { status: 200 }));
    window.fetch = inner as typeof window.fetch;
    installClientVersionFetch();

    await window.fetch("/api/tickets/71bc0580-d091-4849-ab60-481683d69ee5/checklists");
    expect(inner).toHaveBeenCalledOnce();
  });
});

describe("htmlHasText", () => {
  it("is false for empty editor markup", () => {
    expect(htmlHasText("<p></p>")).toBe(false);
    expect(htmlHasText("<p>&nbsp; </p><p><br></p>")).toBe(false);
  });

  it("is true when there is text", () => {
    expect(htmlHasText("<p>Hello</p>")).toBe(true);
    expect(htmlHasText("<p>&lt;script&gt;</p>")).toBe(true);
  });
});
