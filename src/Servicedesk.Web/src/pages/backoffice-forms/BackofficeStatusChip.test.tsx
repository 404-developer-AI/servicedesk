import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { BackofficeStatusChip, backofficeStatusLabel } from "./BackofficeStatusChip";

describe("backoffice status label", () => {
  it("names who is handling or handled the form", () => {
    expect(backofficeStatusLabel({ status: "Open", claimedByName: null, handledByName: null })).toBe("Open");
    expect(backofficeStatusLabel({ status: "InProgress", claimedByName: "Sam Example", handledByName: null }))
      .toBe("In progress · Sam Example");
    expect(backofficeStatusLabel({ status: "Handled", claimedByName: null, handledByName: "Alex Example" }))
      .toBe("Handled · Alex Example");
    expect(backofficeStatusLabel({ status: "InProgress", claimedByName: null, handledByName: null })).toBe("In progress");
  });

  it("renders the chip text", () => {
    render(<BackofficeStatusChip item={{ status: "Handled", claimedByName: null, handledByName: "Alex Example" }} />);
    expect(screen.getByText("Handled · Alex Example")).toBeTruthy();
  });
});
