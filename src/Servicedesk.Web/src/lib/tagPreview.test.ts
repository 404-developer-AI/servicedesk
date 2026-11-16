import { describe, expect, it } from "vitest";
import { isTagPreviewable, setTagPreviewOpen } from "@/lib/tagPreview";

const flag = () => document.documentElement.hasAttribute("data-tag-preview-open");

describe("tag preview open flag", () => {
  it("stays set while any preview is open and clears when the last one closes", () => {
    setTagPreviewOpen("a", true);
    setTagPreviewOpen("b", true);
    expect(flag()).toBe(true);
    setTagPreviewOpen("a", false);
    expect(flag()).toBe(true);
    setTagPreviewOpen("b", false);
    expect(flag()).toBe(false);
  });

  it("closing an id twice (unmount after close) never re-blurs or goes negative", () => {
    setTagPreviewOpen("c", true);
    setTagPreviewOpen("c", false);
    setTagPreviewOpen("c", false);
    expect(flag()).toBe(false);
    setTagPreviewOpen("d", true);
    expect(flag()).toBe(true);
    setTagPreviewOpen("d", false);
  });

  it("only note-like events get a preview", () => {
    expect(isTagPreviewable("Note")).toBe(true);
    expect(isTagPreviewable("Call")).toBe(true);
    expect(isTagPreviewable("ChecklistCloseBlocked")).toBe(false);
    expect(isTagPreviewable(null)).toBe(false);
  });
});
