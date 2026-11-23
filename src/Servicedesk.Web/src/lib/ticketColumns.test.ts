import { describe, expect, it } from "vitest";
import {
  highlightRanges,
  normalizeLayout,
  rowMatchesColumns,
  searchTokens,
} from "./ticketColumns";
import type { TicketListItem } from "./ticket-api";

const row = {
  number: 1042,
  subject: "Printer offline in Café",
  requesterFirstName: "Anaïs",
  requesterLastName: "Peeters",
  requesterEmail: "anais@acme.be",
  companyName: "Acme NV",
  queueName: "Support",
  statusName: "Open",
  priorityName: "High",
  categoryName: null,
  assigneeEmail: null,
} as unknown as TicketListItem;

describe("normalizeLayout", () => {
  it("keeps the saved order and drops unknown or duplicate ids", () => {
    expect(normalizeLayout(["statusName", "bogus", "number", "statusName"])).toEqual(["statusName", "number"]);
  });
});

describe("Columns search", () => {
  it("is case- and accent-insensitive", () => {
    expect(rowMatchesColumns(row, searchTokens("CAFE"), ["subject"])).toBe(true);
    expect(rowMatchesColumns(row, searchTokens("anais"), ["requester"])).toBe(true);
  });

  it("requires every token to match some visible column", () => {
    expect(rowMatchesColumns(row, searchTokens("acme printer"), ["subject", "companyName"])).toBe(true);
    expect(rowMatchesColumns(row, searchTokens("acme printer"), ["subject"])).toBe(false);
  });

  it("ignores hidden columns and non-text columns", () => {
    expect(rowMatchesColumns(row, searchTokens("support"), ["subject"])).toBe(false);
    expect(rowMatchesColumns(row, searchTokens("support"), ["updatedUtc"])).toBe(false);
  });

  it("matches the number with or without #", () => {
    expect(rowMatchesColumns(row, searchTokens("#1042"), ["number"])).toBe(true);
    expect(rowMatchesColumns(row, searchTokens("104"), ["number"])).toBe(true);
  });

  it("highlights on the original text, accents included", () => {
    const text = "Printer offline in Café";
    const [[s, e]] = highlightRanges(text, searchTokens("cafe"));
    expect(text.slice(s, e)).toBe("Café");
  });

  it("merges overlapping highlight ranges", () => {
    expect(highlightRanges("printer", searchTokens("print inter"))).toEqual([[0, 7]]);
  });
});
