import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const systemApi = vi.hoisted(() => ({
  version: vi.fn(),
  time: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  systemApi,
}));

import { Footer } from "./Footer";

function renderFooter() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <Footer />
    </QueryClientProvider>,
  );
}

describe("Footer", () => {
  beforeEach(() => {
    systemApi.version.mockReset();
    systemApi.time.mockReset();
  });

  it("renders the server-sourced version and a server-time clock", async () => {
    systemApi.version.mockResolvedValue({
      version: "0.0.2-test",
      commit: "abc1234",
      buildTime: "2026-04-11T10:00:00Z",
    });
    systemApi.time.mockResolvedValue({
      utc: "2026-04-11T10:30:00Z",
      timezone: "Europe/Amsterdam",
      offsetMinutes: 120,
    });

    renderFooter();

    await waitFor(() => {
      expect(screen.getByTestId("footer-version").textContent).toContain("0.0.2-test");
    });
    await waitFor(() => {
      expect(screen.getByTestId("footer-server-time").textContent).toContain("12:30:00");
    });
    // Timezone rendered verbatim from the server payload.
    expect(screen.getByText("Europe/Amsterdam")).toBeInTheDocument();
  });
});
