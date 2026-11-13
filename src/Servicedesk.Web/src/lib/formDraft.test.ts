import { describe, expect, it } from "vitest";
import type { FormField } from "@/lib/forms-api";
import { draftFromAnswers, draftToInput, emptyDraft, parseDuration, validateDraft } from "@/lib/formDraft";

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
