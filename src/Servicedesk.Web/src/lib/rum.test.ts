import { describe, expect, it } from "vitest";
import { apiTemplate, chunkName } from "./rum";

describe("rum helpers", () => {
  it("reduces API paths to templates without ids", () => {
    expect(apiTemplate("/api/tickets/3f2b8c1e-4d5a-4b6c-9d7e-1a2b3c4d5e6f/events")).toBe("/api/tickets/{id}/events");
    expect(apiTemplate("/api/tickets/12345?x=secret")).toBe("/api/tickets/{id}");
    expect(apiTemplate("/api/search")).toBe("/api/search");
  });

  it("names a chunk without its content hash", () => {
    expect(chunkName("https://sd.example/assets/TicketDetailPage-Ab12Cd34.js")).toBe("TicketDetailPage");
    expect(chunkName("/assets/index-C1Q7WGph.js")).toBe("index");
    expect(chunkName(undefined)).toBe("");
  });
});
