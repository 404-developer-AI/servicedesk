import { useEffect, useState } from "react";
import { OrderDetailDialog } from "./OrderDetailDialog";

/// Order id carried by the custom event the rich-text editor dispatches when an
/// order pill is clicked inside ProseMirror (which swallows the native click).
export const OPEN_ORDER_EVENT = "sd:open-order";

/// Global host for the "::" order pills. Mounted once in the app shell. Two
/// open paths converge here:
///   1. A native click on any rendered `[data-order-id]` element (e.g. a pill
///      inside a saved internal note rendered via SafeHtml) — caught by a
///      capture-phase document listener.
///   2. A `sd:open-order` CustomEvent dispatched by the editor's own click
///      handler, because ProseMirror intercepts clicks on its nodes.
/// Either way we open the shared OrderDetailDialog with the clicked order id.
export function OrderPillHost() {
  const [orderId, setOrderId] = useState<string | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const onDocClick = (e: MouseEvent) => {
      const target = e.target as HTMLElement | null;
      const pill = target?.closest?.("[data-order-id]") as HTMLElement | null;
      if (!pill) return;
      const id = pill.getAttribute("data-order-id");
      if (!id) return;
      e.preventDefault();
      e.stopPropagation();
      setOrderId(id);
      setOpen(true);
    };
    const onCustom = (e: Event) => {
      const id = (e as CustomEvent<string>).detail;
      if (!id) return;
      setOrderId(id);
      setOpen(true);
    };
    // Capture phase so we intercept the pill before any anchor navigation.
    document.addEventListener("click", onDocClick, true);
    window.addEventListener(OPEN_ORDER_EVENT, onCustom as EventListener);
    return () => {
      document.removeEventListener("click", onDocClick, true);
      window.removeEventListener(OPEN_ORDER_EVENT, onCustom as EventListener);
    };
  }, []);

  return <OrderDetailDialog orderId={orderId} open={open} onOpenChange={setOpen} />;
}
