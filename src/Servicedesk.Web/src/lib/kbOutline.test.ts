import { describe, expect, it } from "vitest";
import { buildKbOutline, slugifyHeading } from "./kbOutline";

describe("slugifyHeading", () => {
  it("strips accents and punctuation and prefixes kb-", () => {
    expect(slugifyHeading("Melding van Security Incidenten")).toBe("kb-melding-van-security-incidenten");
    expect(slugifyHeading("Gebruik van CRM-opmerkingen (klantniveau)")).toBe(
      "kb-gebruik-van-crm-opmerkingen-klantniveau",
    );
    expect(slugifyHeading("Efficiëntie & één")).toBe("kb-efficientie-een");
  });

  it("falls back when nothing usable is left", () => {
    expect(slugifyHeading("!!!")).toBe("kb-section");
  });
});

describe("buildKbOutline", () => {
  it("returns the input untouched when there are no h2/h3 headings", () => {
    const html = "<p>Only text</p><h4>Minor</h4>";
    expect(buildKbOutline(html)).toEqual({ html, headings: [] });
  });

  it("assigns ids to h2 and h3 in document order", () => {
    const { html, headings } = buildKbOutline(
      "<h2>Algemeen</h2><p>x</p><h3>Specialist Consult</h3><h3>Tickets</h3>",
    );
    expect(headings).toEqual([
      { id: "kb-algemeen", text: "Algemeen", level: 2 },
      { id: "kb-specialist-consult", text: "Specialist Consult", level: 3 },
      { id: "kb-tickets", text: "Tickets", level: 3 },
    ]);
    expect(html).toContain('<h2 id="kb-algemeen">Algemeen</h2>');
    expect(html).toContain('<h3 id="kb-specialist-consult">');
  });

  it("deduplicates repeated heading text", () => {
    const { headings } = buildKbOutline("<h3>Stappen</h3><h3>Stappen</h3><h3>Stappen</h3>");
    expect(headings.map((h) => h.id)).toEqual(["kb-stappen", "kb-stappen-2", "kb-stappen-3"]);
  });

  it("skips empty headings and keeps inline markup", () => {
    const { html, headings } = buildKbOutline("<h2> </h2><h2><strong>Doel</strong></h2>");
    expect(headings).toEqual([{ id: "kb-doel", text: "Doel", level: 2 }]);
    expect(html).toContain("<strong>Doel</strong>");
  });
});
