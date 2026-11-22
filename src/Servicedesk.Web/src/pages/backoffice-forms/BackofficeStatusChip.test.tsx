import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { BackofficeStatusChip } from "./BackofficeStatusChip";
import { backofficeStatusLabel, presenceName } from "./backofficeLabels";

describe("backoffice status label", () => {
  it("names who is working on or handled the form", () => {
    expect(backofficeStatusLabel({ status: "Open", handledByName: null })).toBe("Open");
    expect(backofficeStatusLabel({ status: "Open", handledByName: null, inProgressBy: "Sam Example" }))
      .toBe("In progress · Sam Example");
    expect(backofficeStatusLabel({ status: "Handled", handledByName: "Alex Example" })).toBe("Handled · Alex Example");
    // Presence never overrides a handled form.
    expect(backofficeStatusLabel({ status: "Handled", handledByName: null, inProgressBy: "Sam Example" })).toBe("Handled");
  });

  it("renders the chip text", () => {
    render(<BackofficeStatusChip item={{ status: "Handled", handledByName: "Alex Example" }} />);
    expect(screen.getByText("Handled · Alex Example")).toBeTruthy();
  });

  it("turns an e-mail into a readable presence name", () => {
    expect(presenceName("sam.example@example.test")).toBe("Sam Example");
    expect(presenceName("Alex")).toBe("Alex");
  });
});
