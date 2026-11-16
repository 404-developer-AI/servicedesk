import * as React from "react";
import { toast } from "sonner";
import {
  FILL_FORM_EVENT,
  OPEN_FORM_EVENT,
  formApiError,
  ticketFormsApi,
  type FillFormDetail,
  type TicketFormDetail,
} from "@/lib/forms-api";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { FormFillDialog, FormViewDialog } from "./FormDialogs";

type FillTarget =
  | { mode: "create"; ticketId: string; templateId: string }
  | { mode: "edit"; ticketId: string; form: TicketFormDetail };

/// v0.1.52 — mounted once per ticket detail page. Every way into a form
/// converges here:
///   - `sd:fill-form` from the `::` picker → fill dialog for a template;
///   - a click on any rendered `[data-form-id]` (the "Open form" pill in the
///     note, the side panel's Forms rows) → view dialog;
///   - `sd:open-form` (programmatic) and `?form=<id>` (global search deep link);
///   - v0.1.55: a minimised draft restored from the tray (`pendingFormRestore`).
/// The server resolves each form to its ticket and checks queue access, so a
/// form id from elsewhere can never be read through this host.
export function TicketFormHost({ ticketId, ticketNumber }: { ticketId: string; ticketNumber?: number | null }) {
  const [fill, setFill] = React.useState<FillTarget | null>(null);
  const [viewId, setViewId] = React.useState<string | null>(null);

  React.useEffect(() => {
    const wanted = new URLSearchParams(window.location.search).get("form");
    setViewId(wanted && /^[0-9a-f-]{36}$/i.test(wanted) ? wanted : null);
    setFill(null);
  }, [ticketId]);

  // Restore a minimised draft of this ticket (the tray navigates here first
  // when it was clicked on another page). An edit draft needs the saved form;
  // the dialog keeps the draft's own base version for the save.
  const pendingRestore = useWorkspaceStore((s) => s.pendingFormRestore);
  React.useEffect(() => {
    if (!pendingRestore) return;
    const store = useWorkspaceStore.getState();
    const entry = store.formDrafts[pendingRestore];
    if (!entry) {
      store.clearFormRestore();
      return;
    }
    if (entry.ticketId !== ticketId) return;
    store.clearFormRestore();
    setViewId(null);
    if (entry.mode === "create" && entry.templateId) {
      setFill({ mode: "create", ticketId, templateId: entry.templateId });
    } else if (entry.mode === "edit" && entry.formId) {
      ticketFormsApi.get(entry.formId).then(
        (form) => setFill({ mode: "edit", ticketId, form }),
        (err) => toast.error(formApiError(err)?.error ?? "This form can no longer be opened."),
      );
    }
  }, [pendingRestore, ticketId]);

  React.useEffect(() => {
    const onDocClick = (e: MouseEvent) => {
      const target = e.target as HTMLElement | null;
      // Inside the rich-text editor the pill is plain text; only rendered
      // notes and the side panel open the dialog.
      if (target?.closest?.(".ProseMirror")) return;
      const el = target?.closest?.("[data-form-id]") as HTMLElement | null;
      const id = el?.getAttribute("data-form-id");
      if (!id) return;
      e.preventDefault();
      e.stopPropagation();
      setViewId(id);
    };
    const onFill = (e: Event) => {
      const d = (e as CustomEvent<FillFormDetail>).detail;
      if (!d || d.ticketId !== ticketId || !d.templateId) return;
      setFill({ mode: "create", ticketId, templateId: d.templateId });
    };
    const onOpen = (e: Event) => {
      const id = (e as CustomEvent<string>).detail;
      if (id) setViewId(id);
    };
    document.addEventListener("click", onDocClick, true);
    window.addEventListener(FILL_FORM_EVENT, onFill as EventListener);
    window.addEventListener(OPEN_FORM_EVENT, onOpen as EventListener);
    return () => {
      document.removeEventListener("click", onDocClick, true);
      window.removeEventListener(FILL_FORM_EVENT, onFill as EventListener);
      window.removeEventListener(OPEN_FORM_EVENT, onOpen as EventListener);
    };
  }, [ticketId]);

  return (
    <>
      <FormViewDialog
        ticketId={ticketId}
        formId={fill ? null : viewId}
        onOpenChange={(o) => { if (!o) setViewId(null); }}
        onEdit={(form) => setFill({ mode: "edit", ticketId, form })}
      />
      <FormFillDialog
        target={fill}
        ticketNumber={ticketNumber}
        onOpenChange={(o) => { if (!o) setFill(null); }}
        onSaved={(saved) => setViewId(fill?.mode === "edit" ? saved.id : null)}
      />
    </>
  );
}
