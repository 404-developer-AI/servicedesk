import { useQuery } from "@tanstack/react-query";
import { ClipboardList } from "lucide-react";
import { Button } from "@/components/ui/button";
import { FILL_FORM_EVENT, ticketFormsApi, type FillFormDetail } from "@/lib/forms-api";

/// v0.1.61 — buttons next to the ticket title for form templates that have a
/// "Ticket header button" label (Settings → Templates → Forms). Only templates
/// usable on this ticket (active, offered in its queue, forms enabled) — the
/// server decides; the query key carries the queue so a move refetches.
/// A click opens the fill dialog through the page's form host, exactly like
/// picking the form with `::`.
export function FormHeaderButtons({ ticketId, queueId }: { ticketId: string; queueId: string }) {
  const q = useQuery({
    queryKey: ["ticket", ticketId, "forms", "header-buttons", queueId],
    queryFn: () => ticketFormsApi.usable(ticketId),
    staleTime: 60_000,
  });
  const buttons = (q.data ?? []).filter((t) => t.headerButtonLabel);
  if (buttons.length === 0) return null;

  return (
    <>
      {buttons.map((t) => (
        <Button
          key={t.id}
          size="sm"
          className="sd-form-header-button h-7 shrink-0 gap-1.5 px-2.5"
          title={t.name}
          onClick={() =>
            window.dispatchEvent(
              new CustomEvent<FillFormDetail>(FILL_FORM_EVENT, { detail: { ticketId, templateId: t.id } }),
            )
          }
        >
          <ClipboardList className="h-4 w-4" />
          {t.headerButtonLabel}
        </Button>
      ))}
    </>
  );
}
