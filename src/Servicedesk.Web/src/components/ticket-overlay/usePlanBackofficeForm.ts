import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { useAuth } from "@/auth/authStore";
import { backofficeFormKeys, backofficeFormsApi, type BackofficeFormItem } from "@/lib/backoffice-forms-api";
import { FILL_FORM_EVENT, FORM_SAVED_EVENT, type FillFormDetail, type FormSavedDetail } from "@/lib/forms-api";
import { finishOverlayTicket } from "@/stores/useTicketOverlayStore";

/// "Plan" on a ticket in the overlay: the back-office request whose template
/// has a plan form (Settings → Templates → Forms → *Plan with form*) opens that
/// form on the ticket. Saving it is what plans — its trigger handles the
/// request — and the overlay then closes (ticket also leaves "Recent").
/// Shared by the lock banner and the overlay's More menu; each caller only
/// reacts to the plan it started. On the full ticket page
/// (`finishOverlay` false) the agent stays on the ticket.
export function usePlanBackofficeForm(
  ticketId: string,
  finishOverlay = true,
): { planForm: BackofficeFormItem | null; plan: () => void } {
  const { user } = useAuth();
  const isBackoffice = !!user?.backofficeFormsEnabled;
  const planningRef = React.useRef<string | null>(null);

  const formsQ = useQuery({
    queryKey: backofficeFormKeys.onTicket(ticketId),
    queryFn: () => backofficeFormsApi.onTicket(ticketId),
    enabled: isBackoffice,
    staleTime: 10_000,
  });
  const planForm = isBackoffice ? (formsQ.data ?? []).find((f) => f.planTemplateId) ?? null : null;

  React.useEffect(() => {
    const onSaved = (e: Event) => {
      const d = (e as CustomEvent<FormSavedDetail>).detail;
      if (!d || d.ticketId !== ticketId || d.mode !== "create") return;
      if (!planningRef.current || d.templateId !== planningRef.current) return;
      planningRef.current = null;
      toast.success("Planned. The request is marked as handled by its trigger.");
      if (finishOverlay) finishOverlayTicket(ticketId);
    };
    window.addEventListener(FORM_SAVED_EVENT, onSaved as EventListener);
    return () => window.removeEventListener(FORM_SAVED_EVENT, onSaved as EventListener);
  }, [ticketId, finishOverlay]);

  const plan = React.useCallback(() => {
    if (!planForm?.planTemplateId) return;
    planningRef.current = planForm.planTemplateId;
    window.dispatchEvent(new CustomEvent<FillFormDetail>(FILL_FORM_EVENT, {
      detail: { ticketId, templateId: planForm.planTemplateId },
    }));
  }, [planForm, ticketId]);

  return { planForm, plan };
}
