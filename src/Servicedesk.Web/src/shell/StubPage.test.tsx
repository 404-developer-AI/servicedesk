import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";

// The MeshSurface pulls in @react-three/fiber which doesn't play well inside
// jsdom. For the StubPage contract we don't actually need WebGL — stub it.
vi.mock("@/shell/MeshBackground", () => ({
  MeshSurface: () => <div data-testid="mesh-surface-stub" />,
}));

import { StubPage } from "./StubPage";

describe("StubPage", () => {
  it("renders title, description and the coming-in badge", () => {
    render(
      <StubPage
        title="Dashboard"
        description="Live metrics and SLA health."
        comingIn="v0.0.13"
      />,
    );

    expect(screen.getByRole("heading", { name: "Dashboard" })).toBeInTheDocument();
    expect(screen.getByText("Live metrics and SLA health.")).toBeInTheDocument();
    expect(screen.getByText(/Coming in v0\.0\.13/)).toBeInTheDocument();
    expect(screen.getByTestId("mesh-surface-stub")).toBeInTheDocument();
  });
});
