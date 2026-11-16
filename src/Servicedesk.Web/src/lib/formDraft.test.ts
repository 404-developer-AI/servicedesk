import { describe, expect, it } from "vitest";
import type { FormField } from "@/lib/forms-api";
import { draftFromAnswers, draftToInput, emptyDraft, parseDuration, shortTextSegments, validateDraft } from "@/lib/formDraft";

const field = (over: Partial<FormField> & Pick<FormField, "id" | "type">): FormField => ({
  label: over.id,
  help: "",
  required: false,
  width: "full",
  defaultValue: "",
  ...over,
});

describe("parseDuration", () => {
  it("accepts h:mm, decimal hours and minutes", () => {
    expect(parseDuration("1:30")).toBe(90);
    expect(parseDuration("1,5")).toBe(90);
    expect(parseDuration("2h")).toBe(120);
    expect(parseDuration("45m")).toBe(45);
    expect(parseDuration("")).toBeNull();
    expect(parseDuration("1:75")).toBeNull();
  });
});

describe("form draft", () => {
  const fields: FormField[] = [
    field({ id: "h", type: "SectionHeader" }),
    field({ id: "serial", type: "ShortText", required: true }),
    field({ id: "users", type: "Number", min: 1, max: 10, decimals: 0 }),
    field({ id: "ok", type: "Checkbox", defaultValue: "true" }),
    field({ id: "tasks", type: "CheckboxGroup", options: [{ id: "a", label: "A" }, { id: "b", label: "B" }] }),
    field({ id: "tech", type: "Agent" }),
    field({ id: "time", type: "Duration" }),
    field({ id: "hw", type: "Article", min: 1 }),
  ];

  it("applies defaults and skips section headers", () => {
    const d = emptyDraft(fields);
    expect(d.h).toBeUndefined();
    expect(d.ok).toEqual({ kind: "bool", value: true });
  });

  it("flags required, range and decimal errors", () => {
    const d = emptyDraft(fields);
    d.users = { kind: "text", value: "11" };
    const errors = validateDraft(fields, d);
    expect(errors.serial).toBe("Required.");
    expect(errors.users).toBe("Between 1 and 10.");

    d.users = { kind: "text", value: "2.5" };
    expect(validateDraft(fields, d).users).toBe("Enter a whole number.");
  });

  it("sends ids only and omits empty answers", () => {
    const d = emptyDraft(fields);
    d.serial = { kind: "text", value: "  SN-1  " };
    d.tech = { kind: "ref", value: { id: "u1", name: "Agent One" } };
    d.time = { kind: "text", value: "1:15" };
    d.hw = { kind: "articles", value: [{ key: "k", articleId: "a1", code: "DOCK", name: "Dock", quantity: "2" }] };
    expect(draftToInput(fields, d)).toEqual({
      serial: "SN-1",
      ok: true,
      tech: "u1",
      time: 75,
      hw: [{ articleId: "a1", quantity: 2 }],
    });
  });

  it("round-trips stored answers without re-applying defaults", () => {
    const d = draftFromAnswers(fields, {
      serial: "SN-1",
      time: 75,
      hw: [{ articleId: "a1", code: "DOCK", name: "Dock", quantity: 2 }],
    });
    expect(d.ok).toEqual({ kind: "bool", value: false });
    expect(d.time).toEqual({ kind: "text", value: "1:15" });
    const input = draftToInput(fields, d);
    expect(input.hw).toEqual([{ articleId: "a1", quantity: 2 }]);
    expect(input.ok).toBe(false);
  });
});

describe("rich-text long text", () => {
  const notes = field({ id: "notes", type: "LongText", required: true });

  it("starts as html with the default turned into escaped paragraphs", () => {
    const d = emptyDraft([{ ...notes, defaultValue: "a <b>\nline 2" }]);
    expect(d.notes).toEqual({ kind: "html", value: "<p>a &lt;b&gt;</p><p>line 2</p>" });
  });

  it("sends rich html and omits an empty editor", () => {
    expect(draftToInput([notes], { notes: { kind: "html", value: "<p><strong>x</strong></p>" } }))
      .toEqual({ notes: { html: "<p><strong>x</strong></p>" } });
    expect(draftToInput([notes], { notes: { kind: "html", value: "<p></p>" } })).toEqual({});
    expect(validateDraft([notes], { notes: { kind: "html", value: "<p> </p>" } })).toEqual({ notes: "Required." });
    expect(validateDraft([notes], { notes: { kind: "html", value: "<p>ok</p>" } })).toEqual({});
  });

  it("sends an untouched legacy answer back as the original string", () => {
    const d = draftFromAnswers([notes], { notes: "line 1\nline 2" });
    expect(d.notes).toMatchObject({ kind: "html", value: "<p>line 1</p><p>line 2</p>" });
    expect(draftToInput([notes], d)).toEqual({ notes: "line 1\nline 2" });
    const edited = { notes: { ...(d.notes as { kind: "html"; value: string }), value: "<p>line 1</p>" } };
    expect(draftToInput([notes], edited)).toEqual({ notes: { html: "<p>line 1</p>" } });
  });

  it("loads a rich answer as html", () => {
    expect(draftFromAnswers([notes], { notes: { html: "<p>x</p>" } }).notes).toEqual({ kind: "html", value: "<p>x</p>" });
  });
});

describe("template variables in defaults", () => {
  const fields: FormField[] = [
    field({ id: "who", type: "ShortText", defaultValue: "{{contact.firstName}} ({{contact.phone}})" }),
    field({ id: "notes", type: "LongText", defaultValue: "For {{company.name}}\n<b>" }),
  ];
  const tokens = { "{{contact.firstName}}": "Jamie", "{{contact.phone}}": "", "{{company.name}}": "Example & Co" };

  it("fills in resolved variables and empties unresolved ones", () => {
    const d = emptyDraft(fields, tokens);
    expect(d.who).toEqual({ kind: "text", value: "Jamie ()" });
    expect(d.notes).toEqual({ kind: "html", value: "<p>For Example &amp; Co</p><p>&lt;b&gt;</p>" });
  });

  it("keeps the raw variables in the admin preview", () => {
    expect(emptyDraft(fields).who).toEqual({ kind: "text", value: "{{contact.firstName}} ({{contact.phone}})" });
  });
});

describe("short text with linked orders", () => {
  const ref = field({ id: "ref", type: "ShortText" });
  const order = { id: "0b8f2c1e-1111-4a2b-9c3d-000000000001", label: "OR12 · Example Ltd" };

  it("sends only orders whose label is still in the text", () => {
    expect(draftToInput([ref], { ref: { kind: "text", value: "See OR12 · Example Ltd", orders: [order] } }))
      .toEqual({ ref: { text: "See OR12 · Example Ltd", orders: [order] } });
    expect(draftToInput([ref], { ref: { kind: "text", value: "See OR1", orders: [order] } }))
      .toEqual({ ref: "See OR1" });
  });

  it("round-trips a stored answer into the draft", () => {
    expect(draftFromAnswers([ref], { ref: { text: "x OR12 · Example Ltd", orders: [order] } }).ref)
      .toEqual({ kind: "text", value: "x OR12 · Example Ltd", orders: [order] });
    expect(draftFromAnswers([ref], { ref: "plain" }).ref).toEqual({ kind: "text", value: "plain" });
  });

  it("splits the text around each order's first free occurrence", () => {
    expect(shortTextSegments("a OR12 · Example Ltd b", [order])).toEqual([
      { text: "a " },
      { text: "OR12 · Example Ltd", order },
      { text: " b" },
    ]);
  });
});
