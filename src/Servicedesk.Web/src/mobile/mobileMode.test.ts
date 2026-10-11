import { afterEach, describe, expect, it, vi } from "vitest";
import { isMobilePath, mobilePathFor, setMobilePreference, shouldUseMobile } from "./mobileMode";

const T = "0b8fdfa9-2c65-46af-8e3f-c6d3582600aa";

function mockPointer(coarse: boolean) {
  vi.stubGlobal(
    "matchMedia",
    (q: string) => ({ matches: q === "(pointer: coarse)" ? coarse : false, media: q, addEventListener() {}, removeEventListener() {} }),
  );
  window.matchMedia = globalThis.matchMedia;
}

describe("mobilePathFor", () => {
  it("maps the pages that have a mobile view", () => {
    expect(mobilePathFor("/", "")).toBe("/m");
    expect(mobilePathFor("/tickets", "?viewId=abc")).toBe("/m/views/abc");
    expect(mobilePathFor("/tickets", "")).toBe("/m");
    expect(mobilePathFor(`/tickets/${T}`, "", "#event-42")).toBe(`/m/tickets/${T}#event-42`);
    expect(mobilePathFor("/search", "?q=printer")).toBe("/m/search?q=printer");
    expect(mobilePathFor("/profile/mentions", "")).toBe("/m/mentions");
    expect(mobilePathFor("/profile", "")).toBe("/m/me");
  });

  it("leaves pages without a mobile view (and ticket sub-pages) on desktop", () => {
    expect(mobilePathFor("/settings/users", "")).toBeNull();
    expect(mobilePathFor(`/tickets/${T}/compose`, "")).toBeNull();
    expect(mobilePathFor(`/tickets/${T}/checklists`, "")).toBeNull();
    expect(mobilePathFor("/contacts/x", "")).toBeNull();
    expect(mobilePathFor("/insights", "")).toBeNull();
  });

  it("drops a hash that is not an event anchor", () => {
    expect(mobilePathFor(`/tickets/${T}`, "", "#something")).toBe(`/m/tickets/${T}`);
  });
});

describe("isMobilePath", () => {
  it("matches /m and its children only", () => {
    expect(isMobilePath("/m")).toBe(true);
    expect(isMobilePath("/m/tickets/1")).toBe(true);
    expect(isMobilePath("/mentions")).toBe(false);
    expect(isMobilePath("/me")).toBe(false);
  });
});

describe("shouldUseMobile", () => {
  afterEach(() => {
    window.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it("is off on a fine-pointer (desktop) device", async () => {
    mockPointer(false);
    expect(await shouldUseMobile()).toBe(false);
  });

  it("follows the admin switch on a touch device", async () => {
    mockPointer(true);
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ autoRedirect: false }), { status: 200 })));
    expect(await shouldUseMobile()).toBe(false);
  });

  it("honours an explicit per-device choice in both directions", async () => {
    mockPointer(true);
    setMobilePreference("desktop");
    expect(await shouldUseMobile()).toBe(false);
    setMobilePreference("mobile");
    expect(await shouldUseMobile()).toBe(true);
  });
});
