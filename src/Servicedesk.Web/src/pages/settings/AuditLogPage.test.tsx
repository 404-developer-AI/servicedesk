import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const auditApi = vi.hoisted(() => ({
  list: vi.fn(),
  get: vi.fn(),
}));

vi.mock("@/lib/api", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api")>("@/lib/api");
  return { ...actual, auditApi };
});

import { AuditLogPage } from "./AuditLogPage";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <AuditLogPage />
    </QueryClientProvider>,
  );
}

const sampleEntry = {
  id: 42,
  utc: "2026-04-11T10:30:00Z",
  actor: "127.0.0.1",
  actorRole: "anon",
  eventType: "rate_limited",
  target: "/api/system/version",
  clientIp: "127.0.0.1",
  userAgent: "vitest",
  payload: { method: "GET" },
  entryHash: "DEADBEEF",
  prevHash: "CAFEBABE",
};

describe("AuditLogPage", () => {
  beforeEach(() => {
    auditApi.list.mockReset();
    auditApi.get.mockReset();
  });

  it("renders rows returned from the audit API", async () => {
    auditApi.list.mockResolvedValue({ items: [sampleEntry], nextCursor: null });

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId("audit-table")).toBeInTheDocument();
    });
    // Target + client ip appear in the row; "127.0.0.1" appears twice (actor
    // column + ip column) so getAllByText is the honest query here.
    expect(screen.getAllByText("127.0.0.1").length).toBeGreaterThan(0);
    expect(screen.getByText("/api/system/version")).toBeInTheDocument();
  });

  it("shows the empty-state message when the API returns no items", async () => {
    auditApi.list.mockResolvedValue({ items: [], nextCursor: null });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/no audit entries match the current filters/i),
      ).toBeInTheDocument();
    });
  });

  it("refetches when the actor filter changes", async () => {
    auditApi.list.mockResolvedValue({ items: [sampleEntry], nextCursor: null });

    renderPage();

    await waitFor(() => expect(auditApi.list).toHaveBeenCalledTimes(1));

    const actorInput = screen.getByPlaceholderText(/username or ip/i);
    fireEvent.change(actorInput, { target: { value: "alice" } });

    await waitFor(() => {
      const lastCall = auditApi.list.mock.calls.at(-1);
      expect(lastCall?.[0]).toMatchObject({ actor: "alice" });
    });
  });
});
