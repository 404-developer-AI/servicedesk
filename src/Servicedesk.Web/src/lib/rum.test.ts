import { describe, expect, it } from "vitest";
import { apiTemplate, chunkName, createRouteChangeTimer } from "./rum";

describe("route change timer", () => {
  const setup = () => {
    let clock = 500; // performance.now() is never 0 once the app runs
    let visible = true;
    const paints: Array<() => void> = [];
    const reports: Array<[string, number]> = [];
    const timer = createRouteChangeTimer({
      now: () => clock,
      visible: () => visible,
      afterPaint: (cb) => paints.push(cb),
      report: (route, ms) => reports.push([route, ms]),
    });
    const paint = () => paints.splice(0).forEach((cb) => cb());
    return {
      timer, reports, paint,
      advance: (ms: number) => (clock += ms),
      setVisible: (v: boolean) => (visible = v),
    };
  };

  it("measures one navigation from navigate to paint", () => {
    const t = setup();
    t.advance(1000);
    t.timer.navigate();
    t.advance(120);
    t.timer.resolved("/tickets/$ticketId");
    t.advance(30);
    t.paint();
    expect(t.reports).toEqual([["/tickets/$ticketId", 150]]);
  });

  it("does not measure a later resolve without a new navigation", () => {
    const t = setup();
    t.timer.navigate();
    t.advance(100);
    t.timer.resolved("/kb");
    t.paint();
    t.advance(120_000);
    t.timer.resolved("/kb");
    t.paint();
    expect(t.reports).toEqual([["/kb", 100]]);
  });

  it("drops the measurement when the tab is hidden before the paint", () => {
    const t = setup();
    t.timer.navigate();
    t.timer.resolved("/timesheet");
    t.timer.hide();
    t.paint();
    t.setVisible(false);
    t.timer.navigate();
    t.timer.resolved("/timesheet");
    t.paint();
    expect(t.reports).toEqual([]);
  });
});

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
