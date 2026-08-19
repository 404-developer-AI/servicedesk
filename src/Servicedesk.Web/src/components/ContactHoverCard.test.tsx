import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const contactApi = vi.hoisted(() => ({
  get: vi.fn(),
  listCompanies: vi.fn(),
}));

vi.mock("@/lib/ticket-api", async () => {
  const actual =
    await vi.importActual<typeof import("@/lib/ticket-api")>("@/lib/ticket-api");
  return { ...actual, contactApi: { ...actual.contactApi, ...contactApi } };
});

import { ContactHoverCard } from "./ContactHoverCard";

const sampleContact = {
  id: "c-1",
  primaryCompanyId: "co-1",
  companyRole: "customer",
  firstName: "Koen",
  lastName: "Beckers",
  email: "koen.beckers@breetec.eu",
  phone: "+32 89 39 67 89",
  mobilePhone: "",
  jobTitle: "Zaakvoerder",
  isActive: true,
  createdUtc: "2026-01-01T00:00:00Z",
  updatedUtc: "2026-01-01T00:00:00Z",
};

function renderCard() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <ContactHoverCard contactId="c-1">
        <span>Koen Beckers</span>
      </ContactHoverCard>
    </QueryClientProvider>,
  );
}

describe("ContactHoverCard", () => {
  beforeEach(() => {
    contactApi.get.mockReset();
    contactApi.listCompanies.mockReset();
  });

  it("opens on hover and shows copyable contact details", async () => {
    contactApi.get.mockResolvedValue(sampleContact);
    contactApi.listCompanies.mockResolvedValue([
      {
        linkId: "l-1",
        companyId: "co-1",
        companyName: "Breetec Group",
        companyCode: "BRE",
        companyShortName: "Breetec",
        companyIsActive: true,
        role: "primary",
      },
    ]);

    renderCard();
    const user = userEvent.setup();
    await user.hover(screen.getByText("Koen Beckers"));

    // openDelay is 350ms; waitFor polls past it with real timers.
    await waitFor(
      () => {
        expect(screen.getByText("koen.beckers@breetec.eu")).toBeInTheDocument();
      },
      { timeout: 3000 },
    );
    expect(screen.getByText("+32 89 39 67 89")).toBeInTheDocument();
    expect(screen.getByText("Breetec Group")).toBeInTheDocument();
    expect(screen.getByText("Zaakvoerder")).toBeInTheDocument();
    expect(contactApi.get).toHaveBeenCalledWith("c-1");
  });

  it("does not fetch anything while the card stays closed", () => {
    renderCard();
    expect(contactApi.get).not.toHaveBeenCalled();
    expect(contactApi.listCompanies).not.toHaveBeenCalled();
  });
});
