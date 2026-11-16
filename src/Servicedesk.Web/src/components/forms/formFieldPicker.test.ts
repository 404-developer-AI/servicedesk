import { describe, expect, it } from "vitest";
import { applyPickerInsert, findPickerTrigger, pickerItemAsText, tokenLabel, variableItems } from "./formFieldPicker";

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
  const tokens = {
    "{{contact.firstName}}": "Jamie",
    "{{contact.phone}}": "",
    "{{company.name}}": "Example Ltd",
  };

  it("labels tokens readably", () => {
    expect(tokenLabel("{{contact.firstName}}")).toBe("Contact · First name");
    expect(tokenLabel("{{ticket.number}}")).toBe("Ticket · Number");
  });

  it("offers only variables with a value, filtered by label or value", () => {
    expect(variableItems(tokens, "").map((i) => i.text)).toEqual(["Jamie", "Example Ltd"]);
    expect(variableItems(tokens, "company").map((i) => i.text)).toEqual(["Example Ltd"]);
    expect(variableItems(tokens, "jam").map((i) => i.name)).toEqual(["Contact · First name"]);
    expect(variableItems(undefined, "")).toEqual([]);
  });

  it("turns a pick into plain text for short text fields", () => {
    expect(pickerItemAsText({ id: "t", name: "Company · Name", kind: "variable", text: "Example Ltd" })).toBe("Example Ltd");
    expect(pickerItemAsText({ id: "k", name: "VPN setup", kind: "kb", href: "https://sd.example.test/kb/articles/k" }))
      .toBe("VPN setup (https://sd.example.test/kb/articles/k)");
    expect(pickerItemAsText({ id: "o", name: "OR3 · Example Ltd", kind: "order" })).toBe("OR3 · Example Ltd");
  });
});
