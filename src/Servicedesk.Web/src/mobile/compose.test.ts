import { afterEach, describe, expect, it } from "vitest";
import {
  activeMentionQuery,
  clearAllDrafts,
  composeHtml,
  loadDraft,
  mentionFromPicker,
  saveDraft,
  splitMentions,
  type ComposeMention,
} from "./compose";

const jan: ComposeMention = { id: "11111111-1111-1111-1111-111111111111", label: "jan", kind: "agent" };
const janP: ComposeMention = { id: "22222222-2222-2222-2222-222222222222", label: "jan.peeters", kind: "agent" };
const sales: ComposeMention = { id: "mbox:33333333-3333-3333-3333-333333333333", label: "Sales", kind: "mailbox" };

describe("composeHtml", () => {
  it("escapes user text and keeps paragraphs and line breaks", () => {
    expect(composeHtml("a <b>&\"x\"\nline 2\n\npara 2", [])).toBe(
      "<p>a &lt;b&gt;&amp;&quot;x&quot;<br>line 2</p><p>para 2</p>",
    );
  });

  it("renders a tagged @label as the desktop mention chip", () => {
    expect(composeHtml("ping @jan please", [jan])).toBe(
      `<p>ping <span data-type="mention" class="sd-mention" data-id="${jan.id}" data-label="jan">@jan</span> please</p>`,
    );
  });

  it("chips a label followed by sentence punctuation", () => {
    expect(composeHtml("Thanks @jan.", [jan])).toBe(
      `<p>Thanks <span data-type="mention" class="sd-mention" data-id="${jan.id}" data-label="jan">@jan</span>.</p>`,
    );
  });

  it("prefers the longest label and never matches inside another label", () => {
    const html = composeHtml("@jan.peeters and @jan", [jan, janP]);
    expect(html).toContain(`data-id="${janP.id}" data-label="jan.peeters">@jan.peeters</span> and `);
    expect(html).toContain(`data-id="${jan.id}" data-label="jan">@jan</span>`);
  });

  it("does not turn an untagged @word or an e-mail address into a chip", () => {
    expect(composeHtml("or @piet", [jan])).toBe(
      `<p>or @piet</p><p><span data-type="mention" class="sd-mention" data-id="${jan.id}" data-label="jan">@jan</span></p>`,
    );
    expect(composeHtml("mail jan@example.com", [jan])).toBe(
      `<p>mail jan@example.com</p><p><span data-type="mention" class="sd-mention" data-id="${jan.id}" data-label="jan">@jan</span></p>`,
    );
  });

  it("still lists a tagged person whose @label was edited out", () => {
    expect(composeHtml("call back tomorrow", [sales])).toBe(
      `<p>call back tomorrow</p><p><span data-type="mention" class="sd-mention" data-id="${sales.id}" data-label="Sales">@Sales</span></p>`,
    );
  });

  it("escapes a hostile label in the chip attributes", () => {
    const evil: ComposeMention = { id: "x\" onmouseover=\"alert(1)", label: "<img>", kind: "agent" };
    const html = composeHtml("", [evil]);
    expect(html).not.toContain("<img>");
    expect(html).not.toContain("\" onmouseover");
  });
});

describe("splitMentions", () => {
  it("routes mailbox ids to their own bucket without the prefix", () => {
    expect(splitMentions([jan, sales])).toEqual({
      userIds: [jan.id],
      mailboxIds: ["33333333-3333-3333-3333-333333333333"],
    });
  });
});

describe("mentionFromPicker", () => {
  it("uses the email local-part for agents and the name for mailboxes", () => {
    expect(mentionFromPicker({ id: "a", email: "jan.peeters@x.be", roleName: "Agent", kind: "agent" }).label).toBe("jan.peeters");
    expect(mentionFromPicker({ id: "mbox:b", email: "sales@x.be", roleName: "Mailbox", kind: "mailbox", label: "Sales" }).label).toBe("Sales");
  });
});

describe("activeMentionQuery", () => {
  it("detects the @@ trigger under the caret", () => {
    expect(activeMentionQuery("hi @@ja", 7)).toEqual({ start: 3, query: "ja" });
    expect(activeMentionQuery("@@", 2)).toEqual({ start: 0, query: "" });
  });

  it("ignores a single @ and an address", () => {
    expect(activeMentionQuery("hi @ja", 6)).toBeNull();
    expect(activeMentionQuery("jan@@x", 6)).toBeNull();
    expect(activeMentionQuery("@@jan done", 10)).toBeNull();
  });
});

describe("drafts", () => {
  afterEach(() => window.localStorage.clear());

  it("keeps a separate slot per kind and ticket", () => {
    saveDraft("note", "t1", { text: "note text", mentions: [jan] });
    saveDraft("call", "t1", { text: "call text", mentions: [] });
    expect(loadDraft("note", "t1")).toEqual({ text: "note text", mentions: [jan] });
    expect(loadDraft("call", "t1")?.text).toBe("call text");
    expect(loadDraft("note", "t2")).toBeNull();
  });

  it("an empty draft removes the slot, sign-out clears every slot", () => {
    saveDraft("note", "t1", { text: "x", mentions: [] });
    saveDraft("note", "t1", { text: "  ", mentions: [] });
    expect(loadDraft("note", "t1")).toBeNull();

    saveDraft("note", "t1", { text: "a", mentions: [] });
    saveDraft("call", "t2", { text: "b", mentions: [] });
    window.localStorage.setItem("sd-theme", "steaan");
    clearAllDrafts();
    expect(loadDraft("note", "t1")).toBeNull();
    expect(loadDraft("call", "t2")).toBeNull();
    expect(window.localStorage.getItem("sd-theme")).toBe("steaan");
  });
});
