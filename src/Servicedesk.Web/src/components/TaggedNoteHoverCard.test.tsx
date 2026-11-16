import { describe, expect, it, vi, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ApiError, ticketApi } from "@/lib/ticket-api";
import { TaggedNoteHoverCard } from "./TaggedNoteHoverCard";

// vi.spyOn rather than a vi.mock factory: with the hoisted-mock variant a
// rejected preview was reported as a test failure even though react-query
// handled it (the component rendered its fallback).
const preview = () => vi.spyOn(ticketApi, "getEventPreview");

const ORDER_ID = "0b8f2c1e-1111-4a2b-9c3d-000000000001";

function renderCard(eventType = "Note") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <TaggedNoteHoverCard ticketId="t-1" eventId={42} eventType={eventType} fallbackText="Short preview">
        <button type="button">Tag row</button>
      </TaggedNoteHoverCard>
    </QueryClientProvider>,
  );
}

describe("TaggedNoteHoverCard", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows the full note on hover, formatted like the timeline", async () => {
    preview().mockResolvedValue({
      ticketNumber: 401574,
      ticketSubject: "Printer offline on the second floor",
      eventId: 42,
      eventType: "Note",
      isInternal: true,
      authorName: "agent@example.test",
      createdUtc: "2026-11-16T10:00:00Z",
      editedUtc: null,
      bodyHtml:
        '<p><strong>Bold</strong> line</p><ul><li>Item</li></ul>' +
        '<p><span data-type="mention" class="sd-mention" data-id="u-1" data-label="pat">@@pat</span> ' +
        `<span data-type="orderMention" class="sd-order-mention" data-order-mention="true" data-order-id="${ORDER_ID}" data-label="OR12">OR12</span></p>` +
        "<script>window.__x = 1</script>",
    });

    renderCard();
    await userEvent.setup().hover(screen.getByText("Tag row"));

    const body = await waitFor(() => screen.getByTestId("tag-note-body"), { timeout: 3000 });
    expect(body.querySelector("strong")?.textContent).toBe("Bold");
    expect(body.querySelector("li")?.textContent).toBe("Item");
    // Mention chip keeps its data attrs and is normalised to "@label".
    expect(body.querySelector(".sd-mention")?.textContent).toBe("@pat");
    // Order pill keeps the id the global OrderPillHost opens.
    expect(body.querySelector(".sd-order-mention")?.getAttribute("data-order-id")).toBe(ORDER_ID);
    expect(body.querySelector("script")).toBeNull();
    expect(screen.getByText("Internal note")).toBeInTheDocument();
    // Header names the ticket the note comes from.
    expect(screen.getByText("#401574")).toBeInTheDocument();
    expect(screen.getByText("Printer offline on the second floor")).toBeInTheDocument();
    expect(ticketApi.getEventPreview).toHaveBeenCalledWith("t-1", 42);
  });

  it("falls back to the stored preview when the note can't be read", async () => {
    preview().mockImplementation(async () => {
      throw new ApiError(404, "/api/tickets/t-1/events/42/preview", "Not found");
    });

    renderCard();
    await userEvent.setup().hover(screen.getByText("Tag row"));

    await waitFor(() => expect(screen.getByText("Short preview")).toBeInTheDocument(), { timeout: 3000 });
    expect(screen.getByText(/Full note not available/)).toBeInTheDocument();
  });

  it("shows the row's hint as a footer in the card", async () => {
    preview().mockResolvedValue({
      ticketNumber: 401574,
      ticketSubject: "Printer offline on the second floor",
      eventId: 42, eventType: "Call", isInternal: true, authorName: null,
      createdUtc: "2026-11-16T10:00:00Z", editedUtc: null, bodyHtml: "<p>Called back</p>",
    });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <TaggedNoteHoverCard ticketId="t-1" eventId={42} eventType="Call" fallbackText={null}
          hint="Click to open the ticket.">
          <button type="button">Tag row</button>
        </TaggedNoteHoverCard>
      </QueryClientProvider>,
    );
    await userEvent.setup().hover(screen.getByText("Tag row"));

    await waitFor(() => expect(screen.getByTestId("tag-note-hint")).toHaveTextContent("Click to open the ticket."), { timeout: 3000 });
    await waitFor(() => expect(screen.getByText("Phone call")).toBeInTheDocument(), { timeout: 3000 });
  });

  it("does not open when the row only gets focus (the tag popover focuses its first row)", async () => {
    const spy = preview();
    renderCard();
    screen.getByText("Tag row").focus();
    await new Promise((r) => setTimeout(r, 600));
    expect(screen.queryByTestId("tag-note-loading")).toBeNull();
    expect(spy).not.toHaveBeenCalled();
  });

  it("adds no hover for rows that are not notes and fetches nothing while closed", () => {
    const spy = preview();
    renderCard("ChecklistCloseBlocked");
    expect(screen.getByText("Tag row")).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });
});
