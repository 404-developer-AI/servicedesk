import { describe, expect, it } from "vitest";
import { applyPickerInsert, findPickerTrigger, pickerItemAsText } from "./formFieldPicker";

describe("findPickerTrigger", () => {
  it("finds the :: query that ends at the caret", () => {
    expect(findPickerTrigger("Serial ::comp", 13)).toEqual({ start: 7, query: "comp" });
    expect(findPickerTrigger("::", 2)).toEqual({ start: 0, query: "" });
  });

  it("ignores a query with a space or a caret elsewhere", () => {
    expect(findPickerTrigger("::comp name", 11)).toBeNull();
    expect(findPickerTrigger("a ::comp b", 10)).toBeNull();
    expect(findPickerTrigger("no trigger", 10)).toBeNull();
  });

  it("replaces the typed query with the pick", () => {
    expect(applyPickerInsert("Hi ::comp!", 3, 9, "Acme")).toEqual({ text: "Hi Acme!", caret: 7 });
  });
});

describe("form field picker items", () => {
  it("turns a pick into plain text for short text fields", () => {
    expect(pickerItemAsText({ id: "k", name: "VPN setup", kind: "kb", href: "https://sd.example.test/kb/articles/k" }))
      .toBe("VPN setup (https://sd.example.test/kb/articles/k)");
    expect(pickerItemAsText({ id: "o", name: "OR3 · Example Ltd", kind: "order" })).toBe("OR3 · Example Ltd");
  });
});

describe("unknownTokens", () => {
  it("lists placeholders outside the supported set", async () => {
    const { unknownTokens } = await import("@/lib/composeTokens");
    expect(unknownTokens("{{company.name}} {{ contact.firstname }}", ["{{company.name}}"])).toEqual(["{{contact.firstname}}"]);
  });
});
