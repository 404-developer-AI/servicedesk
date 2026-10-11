import { describe, expect, it } from "vitest";
import { viewDisplayConfig, viewFilters, viewListQuery } from "./viewFilters";

describe("viewFilters", () => {
  it("reads multi-select lists and folds legacy singular ids", () => {
    expect(
      viewFilters({
        filtersJson: JSON.stringify({ queueIds: ["q1", "q2"], statusId: "s1", openOnly: true, callbacksOnly: true }),
      }),
    ).toEqual({ queueIds: ["q1", "q2"], statusIds: ["s1"], openOnly: true, callbacksOnly: true });
  });

  it("ignores empty lists, wrong types and bad JSON", () => {
    expect(viewFilters({ filtersJson: JSON.stringify({ queueIds: [], priorityIds: [1, ""], openOnly: "yes" }) })).toEqual({});
    expect(viewFilters({ filtersJson: "{nope" })).toEqual({});
  });
});

describe("viewDisplayConfig + viewListQuery", () => {
  it("carries sort and floats into the list query", () => {
    const dc = viewDisplayConfig({
      displayConfigJson: JSON.stringify({ sort: { field: "updatedUtc", direction: "desc" }, priorityFloat: true, callbackFloat: true }),
    });
    expect(viewListQuery({ openOnly: true }, dc)).toEqual({
      openOnly: true,
      sortField: "updatedUtc",
      sortDirection: "desc",
      priorityFloat: true,
      callbackFloat: true,
      researchFloat: undefined,
      stateBucketSort: undefined,
    });
  });

  it("falls back to defaults for an empty or broken config", () => {
    expect(viewDisplayConfig({ displayConfigJson: "" })).toEqual({});
    expect(viewDisplayConfig({ displayConfigJson: "]" })).toEqual({});
  });
});
