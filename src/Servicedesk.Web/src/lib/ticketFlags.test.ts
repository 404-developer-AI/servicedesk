import { describe, expect, it } from "vitest";
import { floatBucket, ticketRowAccentStyle } from "./ticketFlags";

const allFloats = { priorityFloat: true, callbackFloat: true, researchFloat: true };
const colors = { callback: "#22c55e", research: "#3b82f6" };

function t(over: Partial<Parameters<typeof floatBucket>[0]> = {}) {
  return {
    priorityIsDefault: true,
    isCallback: false,
    isResearch: false,
    statusStateCategory: "Open",
    ...over,
  };
}

describe("floatBucket", () => {
  it("applies the fixed precedence Priority > Call-back > Research", () => {
    const all = t({ priorityIsDefault: false, isCallback: true, isResearch: true });
    expect(floatBucket(all, allFloats)).toBe(0);
    expect(floatBucket(t({ isCallback: true, isResearch: true }), allFloats)).toBe(1);
    expect(floatBucket(t({ isResearch: true }), allFloats)).toBe(2);
    expect(floatBucket(t(), allFloats)).toBeNull();
  });

  it("falls through to the next enabled float when a higher one is off", () => {
    const all = t({ priorityIsDefault: false, isCallback: true, isResearch: true });
    expect(floatBucket(all, { callbackFloat: true, researchFloat: true })).toBe(1);
    expect(floatBucket(all, { researchFloat: true })).toBe(2);
    expect(floatBucket(all, {})).toBeNull();
  });

  it("only floats New and Open tickets", () => {
    for (const cat of ["Pending", "Resolved", "Closed"]) {
      expect(floatBucket(t({ isCallback: true, statusStateCategory: cat }), allFloats)).toBeNull();
    }
    expect(floatBucket(t({ isCallback: true, statusStateCategory: "New" }), allFloats)).toBe(1);
  });
});

describe("ticketRowAccentStyle", () => {
  const base = { priorityColor: "#9ca3af", priorityIsDefault: true, isCallback: false, isResearch: false };

  it("keeps a plain ticket on its priority bar without glow", () => {
    const s = ticketRowAccentStyle(base, colors);
    expect(s.boxShadow).toContain("#9ca3af");
    expect(s.backgroundImage).toBeUndefined();
  });

  it("paints call-back and research in their colour with the glow", () => {
    const cb = ticketRowAccentStyle({ ...base, isCallback: true, isResearch: true }, colors);
    expect(cb.boxShadow).toContain("#22c55e");
    expect(cb.backgroundImage).toContain("#22c55e");
    const rs = ticketRowAccentStyle({ ...base, isResearch: true }, colors);
    expect(rs.backgroundImage).toContain("#3b82f6");
  });

  it("lets a non-default priority win over the flags", () => {
    const s = ticketRowAccentStyle(
      { ...base, priorityColor: "#ef4444", priorityIsDefault: false, isCallback: true },
      colors,
    );
    expect(s.boxShadow).toContain("#ef4444");
    expect(s.backgroundImage).toContain("#ef4444");
  });
});
