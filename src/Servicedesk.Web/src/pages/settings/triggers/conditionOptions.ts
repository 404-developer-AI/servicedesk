/// Fixed value lists of the trigger condition editor (moved out of
/// ConditionsEditor.tsx in v0.1.67).

export const SENDER_OPTIONS = [
  { value: "Customer", label: "Customer" },
  { value: "Agent", label: "Agent" },
  { value: "System", label: "System" },
];

/// v0.1.58 — form saves (the `form.save_event` field), any form.
export const FORM_SAVE_EVENT_OPTIONS = [
  { value: "created", label: "Filled in (new form)" },
  { value: "updated", label: "Edited (new version)" },
];

/// v0.1.54 — back office form events (the `form.backoffice_event` field).
export const FORM_EVENT_OPTIONS = [
  { value: "submitted", label: "Submitted to back office" },
  { value: "handled", label: "Handled by back office" },
  { value: "reopened", label: "Reopened (back on the list)" },
];

/// v0.1.59 — what changed on a linked TD SYNNEX order (`order.change`).
/// Values are the server's TdSynnexChangeKind constants.
export const ORDER_CHANGE_OPTIONS = [
  { value: "status", label: "Order status" },
  { value: "ship_date", label: "Expected ship date" },
  { value: "shipment", label: "Shipment / delivery note" },
  { value: "invoice", label: "Invoice" },
  { value: "quantity", label: "Quantity" },
  { value: "lines", label: "Lines added or removed" },
];

/// v0.1.59 — TD SYNNEX order statuses (`order.status`).
export const ORDER_STATUS_OPTIONS = [
  { value: "OPEN", label: "Open" },
  { value: "INVESTIGATION", label: "Investigation" },
  { value: "SHIPPING", label: "Shipping" },
  { value: "COMPLETED", label: "Completed" },
  { value: "REJECTED", label: "Rejected" },
];

export const ARTICLE_TYPE_OPTIONS = [
  { value: "MailReceived", label: "Mail received" },
  { value: "MailSent", label: "Mail sent" },
  { value: "Comment", label: "Comment" },
  { value: "Note", label: "Note" },
  { value: "Call", label: "Phone call" },
  { value: "PortalMessage", label: "Portal message (customer)" },
  // v0.1.67 — a customer answered a campaign through its answer link.
  { value: "CampaignAnswer", label: "Campaign answer (customer)" },
];
