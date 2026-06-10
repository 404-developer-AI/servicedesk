import { csrfHeader } from "@/lib/csrf";

export class ApiError extends Error {
  readonly status: number;
  readonly url: string;
  /// Parsed JSON body of the failed response, if the server returned
  /// application/json. Lets callers branch on error codes (e.g.
  /// "status_gate_required") instead of just status numbers.
  readonly body: unknown;
  constructor(status: number, url: string, message: string, body: unknown = null) {
    super(message);
    this.status = status;
    this.url = url;
    this.body = body;
  }
}

async function request<T>(
  method: string,
  url: string,
  body?: unknown,
): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let parsedBody: unknown = null;
    try {
      const txt = await res.text();
      if (txt) parsedBody = JSON.parse(txt);
    } catch {
      // non-JSON body — surface as null; callers fall back to status.
    }
    throw new ApiError(
      res.status,
      url,
      `${url} → ${res.status} ${res.statusText}`,
      parsedBody,
    );
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

// ---- Ticket types ----

/// Which branch of the mail-intake decision tree (or post-creation action)
/// settled the ticket's company. 'unresolved' pairs with awaitingCompanyAssignment=true.
export type CompanyResolvedVia =
  | "thread_reply"
  | "primary"
  | "secondary"
  | "manual"
  | "unresolved";

export type TicketListItem = {
  id: string;
  number: number;
  subject: string;
  queueId: string;
  queueName: string;
  statusId: string;
  statusName: string;
  statusColor: string;
  statusStateCategory: string;
  priorityId: string;
  priorityName: string;
  priorityLevel: number;
  priorityColor: string;
  priorityIsDefault: boolean;
  requesterContactId: string;
  requesterEmail: string;
  requesterFirstName: string;
  requesterLastName: string;
  requesterCompanyId: string | null;
  companyName: string | null;
  assigneeUserId: string | null;
  assigneeEmail: string | null;
  categoryId: string | null;
  categoryName: string | null;
  createdUtc: string;
  updatedUtc: string;
  dueUtc: string | null;
  // v0.0.74 — snooze/pending-till. Non-null = currently pending until this
  // moment (always future in practice; cleared on elapse). Opt-in column.
  pendingTillUtc: string | null;
  awaitingCompanyAssignment: boolean;
  companyResolvedVia: CompanyResolvedVia | null;
  // v0.0.39 — first-class ticket type. Always set after the migration
  // (NOT NULL at the DB layer); legacy rows are 'support' after backfill.
  ticketTypeId: string;
};

export type TicketPage = {
  items: TicketListItem[];
  nextCursor: { updatedUtc: string; id: string } | null;
  nextOffset: number | null;
};

export type Ticket = {
  id: string;
  number: number;
  subject: string;
  requesterContactId: string;
  assigneeUserId: string | null;
  queueId: string;
  statusId: string;
  priorityId: string;
  categoryId: string | null;
  source: string;
  externalRef: string | null;
  createdUtc: string;
  updatedUtc: string;
  dueUtc: string | null;
  firstResponseUtc: string | null;
  resolvedUtc: string | null;
  closedUtc: string | null;
  isDeleted: boolean;
  companyId: string | null;
  awaitingCompanyAssignment: boolean;
  companyResolvedVia: CompanyResolvedVia | null;
  /// Set on tickets that have been merged into another (v0.0.23). The detail
  /// payload also surfaces `mergedIntoTicketNumber` separately for banner
  /// rendering without a second round-trip.
  mergedIntoTicketId: string | null;
  mergedUtc: string | null;
  mergedByUserId: string | null;
  /// Set on tickets that were split off from another (v0.0.23). The detail
  /// payload surfaces `splitFromTicketNumber` separately for the banner.
  splitFromTicketId: string | null;
  splitFromUtc: string | null;
  splitFromUserId: string | null;
  /// v0.0.37 — agent-visible "Pending till" datetime, set when the
  /// status sits in the Pending state_category. Null otherwise. The
  /// pending-till scheduler wakes the ticket at this UTC moment;
  /// `pendingTillNextTriggerId` is a one-shot pointer used by chained
  /// reminder triggers (see SetPendingTillHandler).
  pendingTillUtc: string | null;
  pendingTillNextTriggerId: string | null;
  /// Manual Main/Sub-ticket link. When set, this ticket is the child of
  /// `parentTicketId`. The detail payload surfaces `parentTicketNumber`
  /// alongside for banner rendering without a second round-trip.
  parentTicketId: string | null;
  parentLinkedUtc: string | null;
  parentLinkedByUserId: string | null;
  /// v0.0.39 — first-class ticket type. Always set after the migration
  /// (NOT NULL at the DB layer, backfilled to 'support' for existing
  /// rows). The header + list use this to render a coloured type badge;
  /// the linked-ticket flow uses it to mark how the ticket was created.
  ticketTypeId: string;
  /// v0.0.41 phase 5 — Zammad migration provenance. Set only on tickets
  /// created by the Zammad importer; both null for local-origin tickets.
  /// The side-panel renders a small "Imported from Zammad #N" badge when
  /// these are populated.
  zammadTicketId: number | null;
  zammadTicketNumber: string | null;
};

export type TicketBody = {
  ticketId: string;
  bodyText: string;
  bodyHtml: string | null;
};

export type TicketEvent = {
  id: number;
  ticketId: string;
  eventType: string;
  authorUserId: string | null;
  authorContactId: string | null;
  authorName: string | null;
  bodyText: string | null;
  bodyHtml: string | null;
  metadataJson: string;
  isInternal: boolean;
  createdUtc: string;
  editedUtc: string | null;
  editedByUserId: string | null;
};

export type TicketEventRevision = {
  id: number;
  eventId: number;
  revisionNumber: number;
  bodyTextBefore: string | null;
  bodyHtmlBefore: string | null;
  isInternalBefore: boolean;
  editedByUserId: string;
  editedByName: string | null;
  editedUtc: string;
};

export type UpdateTicketEventRequest = {
  bodyText?: string;
  bodyHtml?: string;
  isInternal?: boolean;
};

export type TicketEventPin = {
  id: number;
  eventId: number;
  ticketId: string;
  pinnedByUserId: string;
  pinnedByName: string | null;
  remark: string;
  createdUtc: string;
};

export type CompanyAlert = {
  companyId: string;
  companyName: string;
  code: string;
  alertText: string;
  alertOnCreate: boolean;
  alertOnOpen: boolean;
  alertOnOpenMode: "session" | "every";
};

export type TicketDetail = {
  ticket: Ticket;
  body: TicketBody;
  events: TicketEvent[];
  pinnedEvents: TicketEventPin[];
  companyAlert: CompanyAlert | null;
  /// Numbers of tickets that have been merged INTO this ticket (v0.0.23).
  /// Empty array on tickets that have never received a merge — the banner
  /// is hidden in that case. Ordered chronologically by merge time.
  mergedSourceTicketNumbers: number[];
  /// Resolved display name (email) of the agent who performed the merge that
  /// sent THIS ticket into another. Null on tickets that aren't merged.
  mergedByUserName: string | null;
  /// Number of the ticket this one was merged into. Companion to the
  /// `mergedIntoTicketId` on the ticket itself — saves the banner from
  /// having to fetch the target just to render its number.
  mergedIntoTicketNumber: string | null;
  /// Number of the ticket this one was split from (v0.0.23). Null when this
  /// ticket wasn't created by a split.
  splitFromTicketNumber: string | null;
  /// Resolved display name (email) of the agent who performed the split.
  splitFromUserName: string | null;
  /// Tickets that were split off from this one (v0.0.23). Empty array when no
  /// splits have been performed. Carries id+number pairs so the banner can
  /// link straight to each child.
  splitChildren: { id: string; number: number }[];
  /// Non-inline attachments from the source mail of a split ticket (v0.0.23).
  /// Empty array on non-split tickets. URLs route through the source ticket's
  /// mail-attachment endpoint so the bytes never move.
  descriptionAttachments: {
    id: string;
    name: string;
    mimeType: string;
    size: number;
    url: string;
  }[];
  /// Number of the manually-linked parent ("Main ticket"). Null when the
  /// ticket has no parent. Companion to `ticket.parentTicketId`.
  parentTicketNumber: string | null;
  /// Resolved email of the user that last set the parent link.
  parentLinkedByUserName: string | null;
  /// Children manually linked to this ticket via Main/Sub.
  childTickets: { id: string; number: string }[];
};

/// Lightweight row returned by /api/tickets/picker for the merge dialog.
export type TicketPickerItem = {
  id: string;
  number: number;
  subject: string;
  statusId: string;
  statusName: string;
  statusColor: string;
  statusStateCategory: string;
  companyId: string | null;
  companyName: string | null;
  requesterContactId: string;
  requesterEmail: string | null;
  requesterFirstName: string | null;
  requesterLastName: string | null;
};

export type MergeTicketResponse = {
  targetTicketId: string;
  sourceNumber: number;
  targetNumber: number;
  movedEventCount: number;
  crossCustomer: boolean;
};

export type MergeTicketRequest = {
  targetTicketId: string;
  acknowledgedCrossCustomer: boolean;
};

export type SplitTicketRequest = {
  sourceMailEventId: number;
  newSubject: string;
};

export type SplitTicketResponse = {
  newTicketId: string;
  newTicketNumber: number;
  sourceNumber: number;
};

export type CreateTicketResponse = {
  ticket: Ticket;
  companyAlert: CompanyAlert | null;
  showAlertOnCreate: boolean;
};

export type TicketListQuery = {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
  // v0.0.40 polish — multi-select counterparts used by saved views.
  // When non-empty, the singular counterpart is ignored server-side.
  queueIds?: string[];
  statusIds?: string[];
  priorityIds?: string[];
  assigneeUserId?: string;
  requesterContactId?: string;
  companyId?: string;
  search?: string;
  openOnly?: boolean;
  openFirst?: boolean;
  sortField?: string;
  sortDirection?: string;
  priorityFloat?: boolean;
  offset?: number;
  cursorUpdatedUtc?: string;
  cursorId?: string;
  limit?: number;
};

export type CreateTicketRequest = {
  subject: string;
  bodyText?: string;
  bodyHtml?: string;
  requesterContactId: string;
  queueId: string;
  statusId: string;
  priorityId: string;
  categoryId?: string;
  assigneeUserId?: string;
  source?: string;
  /// v0.0.37 — optional initial "Pending till" datetime (UTC ISO).
  /// Only honoured when the initial status sits in the Pending
  /// state_category; otherwise the backend ignores it.
  pendingTillUtc?: string | null;
  /// When set, the newly created ticket is immediately linked as a
  /// sub-ticket of this parent (manual Main/Sub flow from the side panel).
  parentTicketId?: string;
  /// v0.0.39 — caller picks the ticket type explicitly when known.
  /// Null/omitted defaults to the seeded 'support' type server-side, so
  /// the legacy "+ New ticket" flow keeps working unchanged.
  ticketTypeId?: string;
  /// v0.0.39 — optional first event written immediately after the
  /// ticket exists. Used by the "Create linked X ticket" flow to drop
  /// an opening note (internal or public) into the timeline alongside
  /// the ticket body.
  initialNote?: { bodyHtml: string; isInternal: boolean };
  /// v0.0.51 — UI-supplied company when the agent picked one in the
  /// create-time popup. When omitted the backend falls back to the
  /// same cascade mail-intake uses (primary → single secondary →
  /// awaiting). Picking a company here always lands resolved_via='manual'.
  companyId?: string;
  /// v0.0.51 — role for a brand-new contact_companies link, set when
  /// the picked company isn't yet on the requester's link list. Must
  /// be omitted when the picked company is already linked.
  /// 'primary' yields 409 if the contact already has a primary on a
  /// different company.
  newLinkRole?: ContactCompanyRole;
};

// v0.0.39 — server-side preset summary used by the LinkedTicketTypeDialog.
// One row per active manual trigger, denormalised with the ticket-type
// metadata so the picker can render a button without a second round-trip.
export type LinkedTicketPresetSummary = {
  triggerId: string;
  name: string;
  ticketTypeId: string;
  ticketTypeCode: string;
  ticketTypeLabel: string;
  ticketTypeDescription: string;
  ticketTypeIcon: string;
  ticketTypeColor: string;
  sortOrder: number;
};

// v0.0.39 — server-rendered prefill DTO. Subject / bodyHtml / initialNote
// templates are already resolved against the parent ticket's render
// context; requester/company are resolved per the trigger's source
// configuration. The drawer opens with this and the agent can edit
// before submitting via POST /api/tickets.
// v0.0.40 — return shape of an ISO 27001 classification transition.
export type IsoTransitionResponse = {
  ticketId: string;
  fromStatusId: string;
  toStatusId: string;
  noteEventId: number;
};

export type LinkedTicketPrefill = {
  triggerId: string;
  ticketTypeId: string;
  ticketTypeCode: string;
  subject: string;
  bodyHtml: string;
  requesterContactId: string;
  companyId: string | null;
  queueId: string;
  statusId: string;
  priorityId: string;
  categoryId: string | null;
  assigneeUserId: string | null;
  initialNote: { bodyHtml: string; isInternal: boolean } | null;
};

export type TicketFieldUpdate = {
  queueId?: string;
  statusId?: string;
  priorityId?: string;
  categoryId?: string;
  assigneeUserId?: string;
  subject?: string;
  bodyText?: string;
  bodyHtml?: string;
  /// v0.0.37 — sent as UTC ISO string. Pair with a statusId flip into
  /// a Pending status to override the server-computed default; the
  /// trigger-driven `set_pending_till` action runs after this PATCH
  /// commits and overwrites whatever we send here, matching the
  /// "trigger has priority over manual" spec.
  pendingTillUtc?: string | null;
  /// v0.0.42 — gate confirmations from the StatusGate dialog. Set only
  /// when statusId is also set; the server re-evaluates the matching
  /// gates and returns 409 with code "status_gate_required" if any
  /// required confirmation is missing.
  gateConfirmations?: GateConfirmation[];
};

/// v0.0.42 — one matching status-change gate the agent must satisfy
/// before the PATCH applies. Discriminated by `kind`:
///   * `prompt_confirm` — confirmation dialog with inline questions
///     (free-text + yes/no rows). Each question carries an admin-
///     defined `key` referenced as `#{prompt.<key>}` in the note
///     template.
///   * `contact_company_link` — hard-block when the ticket's requester
///     has no `contact_companies` row. The dialog renders a company-
///     picker + role selector; submitting creates the link inside the
///     same PATCH that flips the status.
export type StatusGateMatch =
  | StatusGatePromptConfirm
  | StatusGateContactCompanyLink;

type StatusGateCommon = {
  triggerId: string;
  name: string;
  title: string;
  /// Null when the admin disabled the message section; the dialog
  /// renders title + body + buttons only.
  message: string | null;
  confirmLabel: string;
  cancelLabel: string;
};

export type StatusGatePromptConfirm = StatusGateCommon & {
  kind: "prompt_confirm";
  questions: GateQuestion[];
};

export type StatusGateContactCompanyLink = StatusGateCommon & {
  kind: "contact_company_link";
  /// The ticket's requester contact id — the link will be attached to
  /// this contact when the dialog is confirmed.
  requesterContactId: string;
  /// Friendly display name for the requester ("First Last" or email).
  /// Rendered in the dialog so the agent knows which contact they're
  /// linking.
  requesterDisplayName: string;
};

export type GateQuestion =
  | {
      key: string;
      type: "text";
      label: string;
      required: boolean;
      yesLabel: null;
      noLabel: null;
    }
  | {
      key: string;
      type: "yesno";
      label: string;
      required: boolean;
      /// Null = the Yes button is hidden. A yesno question with only
      /// `noLabel` set offers no positive-confirmation path; the gate's
      /// bottom Confirm button proceeds regardless of that question.
      yesLabel: string | null;
      noLabel: string | null;
    };

/// One per-trigger confirmation packet shipped back to the PATCH
/// endpoint. The payload is kind-specific:
///   * `prompt_confirm` → `answers` keyed by the gate's per-question
///     `key`; values come from the dialog (typed text or clicked
///     positive button's label).
///   * `contact_company_link` → `companyId` + `role` identify the link
///     to create. The server creates the row before applying the
///     status flip; invalid company/role yields 400/409.
export type GateConfirmation = {
  triggerId: string;
  answers?: Record<string, string>;
  companyId?: string;
  role?: ContactCompanyRole;
};

/// First-open title-review gate. Surfaced by the open-gate probe when an
/// agent opens a ticket whose title hasn't been reviewed yet. Drives a
/// blocking dialog: the agent reviews/edits `currentSubject` and confirms.
export type OpenGateMatch = {
  triggerId: string;
  name: string;
  /// Dialog heading.
  title: string;
  /// Optional question shown above the editable field; null when disabled.
  message: string | null;
  /// Label above the editable subject field.
  fieldLabel: string;
  /// The single approve button's label.
  confirmLabel: string;
  /// The ticket's current subject, pre-filled into the editable field.
  currentSubject: string;
  /// When true the dialog renders the original-request panel above the
  /// title field so the agent can read what the ticket is about first.
  showRequest: boolean;
  /// The ticket's original request body (HTML, sanitised on render);
  /// null when the panel is disabled or there is no HTML body.
  requestBodyHtml: string | null;
  /// Plain-text fallback for the original request; null when HTML is
  /// present or the panel is disabled.
  requestBodyText: string | null;
};

export type NewTicketEvent = {
  eventType: "Comment" | "Note";
  bodyText?: string;
  bodyHtml?: string;
  isInternal?: boolean;
  /// Ids of attachments uploaded via /api/tickets/{id}/attachments while the
  /// post was being composed. The server flips them onto the freshly-created
  /// event in the same request; mismatched ids (other ticket, already linked)
  /// are silently dropped so a stale draft can't fail the submit.
  attachmentIds?: string[];
  /// Agent user-ids tagged via @@-mention in the editor body. Filtered
  /// server-side against the Agent+Admin set; unknown / customer / deleted
  /// ids are silently dropped before the event metadata is written.
  mentionedUserIds?: string[];
  /// Tagging-only mailbox ids tagged via @@-mention. Filtered to active rows
  /// server-side; each receives a notification mail (no in-app entry).
  mentionedMailboxIds?: string[];
};

export type TicketAttachmentMeta = {
  id: string;
  url: string;
  mimeType: string;
  size: number;
  filename: string;
};

export type OutboundMailKind = "Reply" | "ReplyAll" | "New" | "Forward";

export type MailRecipientInput = {
  address: string;
  name?: string;
};

export type SendOutboundMailRequest = {
  kind: OutboundMailKind;
  to?: MailRecipientInput[];
  cc?: MailRecipientInput[];
  bcc?: MailRecipientInput[];
  subject: string;
  bodyHtml: string;
  /// Attachments uploaded via /api/tickets/{id}/attachments while composing.
  /// Server resolves them, detects inline (URL appears in bodyHtml AND mime
  /// is image/*), rewrites those URLs to cid:{generated}, and ships the
  /// rest as plain Graph file-attachments. Total bytes capped at
  /// Mail.MaxOutboundTotalBytes — exceeding it returns 413.
  attachmentIds?: string[];
  /// Agent user-ids tagged via @@-mention. See NewTicketEvent for semantics.
  mentionedUserIds?: string[];
  /// Tagging-only mailbox ids tagged via @@-mention. See NewTicketEvent.
  mentionedMailboxIds?: string[];
  /// Intake-form instance ids embedded via `::`-mention (v0.0.19). Each id
  /// must be a Draft instance owned by this ticket; the server mints a
  /// token, embeds the public link in the body and atomically flips the
  /// instance to Sent + writes an IntakeFormSent ticket event.
  linkedFormIds?: string[];
  /// v0.0.61 — true when the compose window pre-loaded the signature as a fixed
  /// block, so the body carries a `data-sd-signature` marker the server swaps
  /// for the real signature (above the quoted history). When false the server
  /// keeps the legacy append-at-the-bottom behaviour.
  signaturePreloaded?: boolean;
};

// ---- Views ----

export type DisplayConfig = {
  priorityFloat?: boolean;
  groupBy?: string | null;
  groupOrder?: string[] | null;
  sort?: { field: string; direction: "asc" | "desc" } | null;
};

export type View = {
  id: string;
  userId: string;
  name: string;
  filtersJson: string;
  columns: string | null;
  sortOrder: number;
  isShared: boolean;
  displayConfigJson: string;
  createdUtc: string;
  updatedUtc: string;
};

export type ViewInput = {
  name: string;
  filtersJson?: string;
  columns?: string | null;
  sortOrder?: number;
  isShared?: boolean;
  displayConfigJson?: string;
};

// ---- Users ----

export type AgentUser = {
  id: string;
  email: string;
  roleName: string;
};

// ---- Tagging-only mailboxes (v0.0.60) ----

/// A login-less @@-mention target. Mentioning it mails a notification to its
/// address; it has no user row, role or tickets. Managed under Settings → Users.
export type TaggingMailbox = {
  id: string;
  name: string;
  email: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
};

export type TaggingMailboxInput = {
  name: string;
  email: string;
  isActive: boolean;
};

/// Slim row returned by the @@-picker search endpoint.
export type TaggingMailboxSearchItem = {
  id: string;
  name: string;
  email: string;
};

/// Unified item the @@-mention popover renders. Agents and tagging mailboxes
/// are merged into one list; `kind` drives the badge + how the node id is
/// serialised (mailbox ids are prefixed `mbox:` so the submit-time split can
/// route them to mentionedMailboxIds without a node-schema change).
export type MentionPickerItem = {
  id: string;
  email: string;
  roleName: string;
  kind: "agent" | "mailbox";
  /// Display label; mailboxes use their friendly name, agents the email local-part.
  label?: string;
};

// ---- Contacts ----

export type Contact = {
  id: string;
  /// The id of the company the contact is linked to with role='primary'.
  /// Read-only — server computes this from the contact_companies join table.
  /// A contact may also have secondary/supplier links, but those live on
  /// the contact-detail view, not here.
  primaryCompanyId: string | null;
  companyRole: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  /// v0.0.28 — mirrors the Adsolut `mobilePhone` field. Empty string when
  /// not set; renders as a hidden row in the contact-detail UI.
  mobilePhone: string;
  jobTitle: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
};

/// Row shape returned by /api/contacts/browse — the paginated overview
/// that backs the `/contacts` page. Flattens the primary-link metadata onto
/// the contact row and adds `extraLinkCount` + `lastTicketUpdatedUtc` so
/// the table can render without N+1 round-trips.
export type ContactListItem = {
  id: string;
  companyRole: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  mobilePhone: string;
  jobTitle: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
  primaryCompanyId: string | null;
  primaryCompanyName: string | null;
  primaryCompanyCode: string | null;
  primaryCompanyShortName: string | null;
  primaryCompanyIsActive: boolean;
  extraLinkCount: number;
  lastTicketUpdatedUtc: string | null;
};

export type ContactOverviewPage = {
  items: ContactListItem[];
  total: number;
  page: number;
  pageSize: number;
};

export type ContactAuditEntry = {
  id: number;
  utc: string;
  actor: string;
  actorRole: string;
  eventType: string;
  target: string | null;
  payloadJson: string;
};

export type ContactAuditPage = {
  items: ContactAuditEntry[];
  nextCursor: number | null;
};

export type ContactBrowseQuery = {
  search?: string;
  companyId?: string;
  role?: ContactCompanyRole | "none";
  includeInactive?: boolean;
  sort?: "name_asc" | "email_asc" | "last_activity_desc";
  page?: number;
  pageSize?: number;
};

export type ContactInput = {
  email: string;
  /// Shorthand on create/update: when set the server also inserts a
  /// link to this company in the same transaction. On POST /api/contacts
  /// the role below decides whether it's primary/secondary/supplier; on
  /// PUT the link is always upserted as 'primary' (legacy behaviour).
  companyId?: string | null;
  /// Only honoured on POST when companyId is set. Defaults to 'primary'.
  role?: ContactCompanyRole;
  companyRole?: string;
  firstName?: string;
  lastName?: string;
  phone?: string;
  mobilePhone?: string;
  jobTitle?: string;
  isActive?: boolean;
};

export type Company = {
  id: string;
  name: string;
  description: string;
  website: string;
  phone: string;
  email: string;
  addressLine1: string;
  addressLine2: string;
  city: string;
  postalCode: string;
  country: string;
  isActive: boolean;
  createdUtc: string;
  updatedUtc: string;
  code: string;
  shortName: string;
  vatNumber: string;
  alertText: string;
  alertOnCreate: boolean;
  alertOnOpen: boolean;
  alertOnOpenMode: "session" | "every";
  /// v0.0.26 — populated when the company is linked to an Adsolut customer.
  /// Null on locally-created rows or installs without the integration.
  adsolutId: string | null;
  /// Last lastModified timestamp seen on the matching Adsolut row. Used by
  /// the per-company "Synced from Adsolut" pill to render "synced X ago"
  /// without an extra round-trip.
  adsolutLastModified: string | null;
};

export type CompanyInput = {
  name: string;
  code: string;
  shortName?: string;
  vatNumber?: string;
  email?: string;
  description?: string;
  website?: string;
  phone?: string;
  addressLine1?: string;
  addressLine2?: string;
  city?: string;
  postalCode?: string;
  country?: string;
  isActive?: boolean;
  alertText?: string;
  alertOnCreate?: boolean;
  alertOnOpen?: boolean;
  alertOnOpenMode?: "session" | "every";
};

export type CompanyDomain = {
  id: string;
  companyId: string;
  domain: string;
  createdUtc: string;
};

export type CompanyDetail = {
  company: Company;
  domains: CompanyDomain[];
};

export type ContactCompanyRole = "primary" | "secondary" | "supplier";

/// One role-tagged link between a contact and a company. Returned by
/// /api/companies/{id}/links for the Contacts-tab so each contact row can
/// render with its role badge relative to *this* company.
export type ContactCompanyLink = {
  id: string;
  contactId: string;
  companyId: string;
  role: ContactCompanyRole;
  createdUtc: string;
  updatedUtc: string;
};

/// One entry in the contact's role-annotated company list — used by the
/// ticket company-assignment dialog and future contact-detail view.
export type ContactCompanyOption = {
  linkId: string;
  companyId: string;
  companyName: string;
  companyCode: string;
  companyShortName: string;
  companyIsActive: boolean;
  role: ContactCompanyRole;
};

/// Minimal company row returned by the agent-readable picker endpoint.
export type CompanyPickerItem = {
  id: string;
  name: string;
  code: string;
  shortName: string;
  isActive: boolean;
};

export type AssignTicketCompanyRequest = {
  companyId: string;
  /// v0.0.51 — role for a new contact_companies link when the picked
  /// company isn't yet on the contact. Must be omitted when the
  /// company is already linked. 'primary' is rejected (409) if the
  /// contact already has a primary on a different company.
  newLinkRole?: ContactCompanyRole;
};

// ---- API functions ----

export const ticketApi = {
  list: (query: TicketListQuery = {}) => {
    const params = new URLSearchParams();
    if (query.queueId) params.set("queueId", query.queueId);
    if (query.statusId) params.set("statusId", query.statusId);
    if (query.priorityId) params.set("priorityId", query.priorityId);
    if (query.queueIds && query.queueIds.length > 0)
      params.set("queueIds", query.queueIds.join(","));
    if (query.statusIds && query.statusIds.length > 0)
      params.set("statusIds", query.statusIds.join(","));
    if (query.priorityIds && query.priorityIds.length > 0)
      params.set("priorityIds", query.priorityIds.join(","));
    if (query.assigneeUserId)
      params.set("assigneeUserId", query.assigneeUserId);
    if (query.requesterContactId)
      params.set("requesterContactId", query.requesterContactId);
    if (query.companyId) params.set("companyId", query.companyId);
    if (query.search) params.set("search", query.search);
    if (query.openOnly) params.set("openOnly", "true");
    if (query.openFirst) params.set("openFirst", "true");
    if (query.sortField) params.set("sortField", query.sortField);
    if (query.sortDirection) params.set("sortDirection", query.sortDirection);
    if (query.priorityFloat) params.set("priorityFloat", "true");
    if (query.offset != null) params.set("offset", String(query.offset));
    if (query.cursorUpdatedUtc)
      params.set("cursorUpdatedUtc", query.cursorUpdatedUtc);
    if (query.cursorId) params.set("cursorId", query.cursorId);
    if (query.limit) params.set("limit", String(query.limit));
    const qs = params.toString();
    return request<TicketPage>("GET", `/api/tickets${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<TicketDetail>("GET", `/api/tickets/${id}`),
  create: (input: CreateTicketRequest) =>
    request<CreateTicketResponse>("POST", "/api/tickets", input),
  update: (id: string, fields: TicketFieldUpdate) =>
    request<TicketDetail>("PATCH", `/api/tickets/${id}`, fields),
  listStatusGates: (id: string, toStatusId: string) =>
    request<{ items: StatusGateMatch[] }>(
      "GET",
      `/api/tickets/${id}/status-gates?toStatusId=${encodeURIComponent(toStatusId)}`,
    ),
  /// First-open title-review gate probe. Returns { gate: null } when the
  /// ticket was already reviewed or no gate matches.
  listOpenGates: (id: string) =>
    request<{ gate: OpenGateMatch | null }>(
      "GET",
      `/api/tickets/${id}/open-gates`,
    ),
  /// Confirm a first-open title-review gate: applies the (possibly edited)
  /// subject, marks the ticket reviewed, runs the trigger's actions, and
  /// returns the refreshed detail.
  confirmOpenGate: (id: string, triggerId: string, subject: string) =>
    request<TicketDetail & { reviewed: boolean }>(
      "POST",
      `/api/tickets/${id}/open-gates/confirm`,
      { triggerId, subject },
    ),
  addEvent: (id: string, event: NewTicketEvent) =>
    request<TicketEvent>("POST", `/api/tickets/${id}/events`, event),
  sendMail: (id: string, payload: SendOutboundMailRequest) =>
    request<TicketEvent>("POST", `/api/tickets/${id}/mail`, payload),
  /// v0.0.61 — resolved signature preview (self-contained data-URI HTML) to
  /// pre-load into the compose window, or { html: null } when nothing should
  /// be pre-loaded. `reply` mirrors the send-time reply gating.
  composeSignature: (id: string, reply: boolean) =>
    request<{ html: string | null }>(
      "GET",
      `/api/tickets/${id}/compose-signature?reply=${reply ? "true" : "false"}`,
    ),
  updateEvent: (id: string, eventId: number, body: UpdateTicketEventRequest) =>
    request<TicketEvent>("PUT", `/api/tickets/${id}/events/${eventId}`, body),
  getEventRevisions: (id: string, eventId: number) =>
    request<TicketEventRevision[]>("GET", `/api/tickets/${id}/events/${eventId}/revisions`),
  pinEvent: (id: string, eventId: number, remark?: string) =>
    request<TicketEventPin>("POST", `/api/tickets/${id}/events/${eventId}/pin`, { remark }),
  unpinEvent: (id: string, eventId: number) =>
    request<void>("DELETE", `/api/tickets/${id}/events/${eventId}/pin`),
  updatePinRemark: (id: string, eventId: number, remark: string) =>
    request<TicketEventPin>("PATCH", `/api/tickets/${id}/events/${eventId}/pin`, { remark }),
  assignCompany: (id: string, body: AssignTicketCompanyRequest) =>
    request<TicketDetail>("PATCH", `/api/tickets/${id}/company`, body),
  changeRequester: (id: string, contactId: string) =>
    request<TicketDetail>("PATCH", `/api/tickets/${id}/requester`, { contactId }),
  picker: (q?: string, excludeTicketId?: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    if (excludeTicketId) params.set("excludeTicketId", excludeTicketId);
    params.set("limit", String(limit));
    return request<{ items: TicketPickerItem[] }>(
      "GET",
      `/api/tickets/picker?${params.toString()}`,
    );
  },
  merge: (id: string, body: MergeTicketRequest) =>
    request<MergeTicketResponse>("POST", `/api/tickets/${id}/merge`, body),
  linkParent: (id: string, parentTicketId: string) =>
    request<{ parentTicketId: string; parentNumber: number }>(
      "POST",
      `/api/tickets/${id}/link-parent`,
      { parentTicketId },
    ),
  unlinkParent: (id: string) =>
    request<void>("DELETE", `/api/tickets/${id}/link-parent`),
  // v0.0.39 — linked-ticket presets (manual triggers grouped by type).
  listLinkedTicketPresets: (parentTicketId: string) =>
    request<LinkedTicketPresetSummary[]>(
      "GET",
      `/api/tickets/${parentTicketId}/linked-ticket-presets`,
    ),
  getLinkedTicketPrefill: (parentTicketId: string, triggerId: string) =>
    request<LinkedTicketPrefill>(
      "GET",
      `/api/tickets/${parentTicketId}/linked-ticket-presets/${triggerId}/prefill`,
    ),

  // v0.0.40 — ISO 27001 classification transitions. Each takes a
  // mandatory motivation that lands as an internal note on the
  // ticket plus a structured audit row.
  isoClassifyEvent: (ticketId: string, motivation: string) =>
    request<IsoTransitionResponse>(
      "POST",
      `/api/tickets/${ticketId}/iso/classify-event`,
      { motivation },
    ),
  isoClassifyIncident: (ticketId: string, motivation: string) =>
    request<IsoTransitionResponse>(
      "POST",
      `/api/tickets/${ticketId}/iso/classify-incident`,
      { motivation },
    ),
  split: (id: string, body: SplitTicketRequest) =>
    request<SplitTicketResponse>("POST", `/api/tickets/${id}/split`, body),
  exportPdf: (id: string, excludeInternal = true) => {
    const params = new URLSearchParams();
    if (!excludeInternal) params.set("excludeInternal", "false");
    const qs = params.toString();
    return `/api/tickets/${id}/export/pdf${qs ? `?${qs}` : ""}`;
  },
  /// Multipart upload of a single file. The server streams to IBlobStore,
  /// sniffs MIME, enforces the size cap, and returns metadata + the
  /// session-cookie-authenticated URL the editor can use as <img src>.
  uploadAttachment: async (
    ticketId: string,
    file: File,
  ): Promise<TicketAttachmentMeta> => {
    const form = new FormData();
    form.append("file", file, file.name);
    const url = `/api/tickets/${ticketId}/attachments`;
    const res = await fetch(url, {
      method: "POST",
      credentials: "include",
      headers: csrfHeader(),
      body: form,
    });
    if (!res.ok) {
      let message = `Upload failed (${res.status})`;
      try {
        const body = (await res.json()) as { error?: string };
        if (body?.error) message = body.error;
      } catch {
        /* ignore */
      }
      throw new ApiError(res.status, url, message);
    }
    return (await res.json()) as TicketAttachmentMeta;
  },
};

export const viewApi = {
  list: () => request<View[]>("GET", "/api/views"),
  // Admin-only: every view in the system, used by management surfaces.
  // Personal `list()` is filtered for admins too so the sidebar stays clean.
  listAll: () => request<View[]>("GET", "/api/views/all"),
  get: (id: string) => request<View>("GET", `/api/views/${id}`),
  create: (input: ViewInput) => request<View>("POST", "/api/views", input),
  update: (id: string, input: ViewInput) =>
    request<View>("PUT", `/api/views/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/views/${id}`),
};

export const userApi = {
  listAgents: () => request<AgentUser[]>("GET", "/api/users"),
  /// Typeahead for the @@-mention popover. Agent + Admin only; customers
  /// never appear. Empty `q` returns the top-N alphabetically.
  searchAgents: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<AgentUser[]>("GET", `/api/users/agents/search?${params.toString()}`);
  },
};

// ---- Statistics (v0.0.69) -------------------------------------------
// Light tile builder. Read surface: list assigned tiles + per-tile data +
// save layout. Write surface: catalogue + tile CRUD + assignments.

export type StatisticMetricDescriptor = {
  key: string;
  label: string;
  unit: string;
  chartTypes: string[];
  groupings: string[];
  supportsScope: boolean;
};

export type StatisticTileDto = {
  id: string;
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserEmail?: string | null;
  position: number;
  size: string;
  hidden: boolean;
};

export type StatisticTileSummary = {
  id: string;
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserEmail: string | null;
  assignedCount: number;
};

export type StatisticBareTile = {
  id: string;
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserIds: string[];
};

export type StatisticDataPoint = {
  label: string;
  value: number;
  value2?: number | null;
  segments?: number[] | null;
};

export type StatisticTileData = {
  tileId: string;
  metricKey: string;
  chartType: string;
  unit: string;
  periodLabel: string;
  total: number;
  points: StatisticDataPoint[];
  seriesLabels?: string[] | null;
  generatedUtc: string;
};

export type StatisticTileInput = {
  title: string;
  metricKey: string;
  chartType: string;
  period: string;
  grouping: string;
  scope: string;
  scopeUserId: string | null;
  scopeUserIds?: string[];
};

export type StatisticLayoutEntry = { tileId: string; size: string; hidden: boolean };

export const statisticsApi = {
  // read surface
  listAssigned: () => request<StatisticTileDto[]>("GET", "/api/statistics/tiles"),
  tileData: (id: string, offset = 0) =>
    request<StatisticTileData>("GET", `/api/statistics/tiles/${id}/data?offset=${offset}`),
  saveLayout: (tiles: StatisticLayoutEntry[]) =>
    request<void>("PUT", "/api/statistics/layout", { tiles }),
  // write surface (builder)
  catalogue: () => request<StatisticMetricDescriptor[]>("GET", "/api/statistics/catalogue"),
  manageList: () => request<StatisticTileSummary[]>("GET", "/api/statistics/manage/tiles"),
  manageGet: (id: string) =>
    request<{ tile: StatisticBareTile; assignedUserIds: string[] }>(
      "GET",
      `/api/statistics/manage/tiles/${id}`,
    ),
  create: (input: StatisticTileInput) =>
    request<StatisticBareTile>("POST", "/api/statistics/manage/tiles", input),
  update: (id: string, input: StatisticTileInput) =>
    request<StatisticBareTile>("PUT", `/api/statistics/manage/tiles/${id}`, input),
  remove: (id: string) =>
    request<void>("DELETE", `/api/statistics/manage/tiles/${id}`),
  setAssignments: (id: string, userIds: string[]) =>
    request<{ assignedUserIds: string[] }>(
      "PUT",
      `/api/statistics/manage/tiles/${id}/assignments`,
      { userIds },
    ),
};

// ---- Tagging-only mailboxes (v0.0.60) -------------------------------

export const taggingMailboxApi = {
  // Admin-only CRUD, surfaced as the first card on Settings → Users.
  list: () => request<TaggingMailbox[]>("GET", "/api/settings/tagging-mailboxes"),
  create: (input: TaggingMailboxInput) =>
    request<TaggingMailbox>("POST", "/api/settings/tagging-mailboxes", input),
  update: (id: string, input: TaggingMailboxInput) =>
    request<TaggingMailbox>("PUT", `/api/settings/tagging-mailboxes/${id}`, input),
  remove: (id: string) =>
    request<void>("DELETE", `/api/settings/tagging-mailboxes/${id}`),
  /// Active-only typeahead for the @@-picker (agent-accessible).
  searchForMention: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<TaggingMailboxSearchItem[]>(
      "GET",
      `/api/tagging-mailboxes/search?${params.toString()}`,
    );
  },
};

// ---- Inbound mailbox polling (v0.0.60, per-source since v0.0.66) -----

/// One row per inbound-mailbox source (a queue can have several). The toggle
/// pauses or resumes Graph polling for that source; delta-state is kept on
/// pause so resuming continues from where it left off.
export type InboundMailbox = {
  sourceId: string;
  queueId: string;
  queueName: string;
  mailbox: string;
  folderName: string | null;
  folderConfigured: boolean;
  isActive: boolean;
  pollingEnabled: boolean;
  lastPolledUtc: string | null;
  lastError: string | null;
  consecutiveFailures: number;
};

export const mailMailboxApi = {
  list: () => request<InboundMailbox[]>("GET", "/api/admin/mail/mailboxes"),
  setPolling: (sourceId: string, enabled: boolean) =>
    request<{ sourceId: string; pollingEnabled: boolean }>(
      "PUT",
      `/api/admin/mail/mailboxes/${sourceId}/polling`,
      { enabled },
    ),
};

/// Merged @@-mention typeahead: agents first, then tagging mailboxes. Mailbox
/// ids are prefixed `mbox:` so the submit-time split routes them correctly.
export const mentionApi = {
  search: async (q: string): Promise<MentionPickerItem[]> => {
    const [agents, mailboxes] = await Promise.all([
      userApi.searchAgents(q),
      taggingMailboxApi.searchForMention(q),
    ]);
    const agentItems: MentionPickerItem[] = agents.map((a) => ({
      id: a.id,
      email: a.email,
      roleName: a.roleName,
      kind: "agent",
    }));
    const mailboxItems: MentionPickerItem[] = mailboxes.map((m) => ({
      id: `mbox:${m.id}`,
      email: m.email,
      roleName: "Mailbox",
      kind: "mailbox",
      label: m.name,
    }));
    return [...agentItems, ...mailboxItems];
  },
};

// ---- Admin user-management (v0.0.13 step 3) --------------------------

export type UserAdminRow = {
  id: string;
  email: string;
  role: "Agent" | "Admin" | "Customer";
  authMode: "Local" | "Microsoft";
  externalSubject: string | null;
  isActive: boolean;
  twoFactorEnabled: boolean;
  createdUtc: string;
  lastLoginUtc: string | null;
  timesheetEnabled: boolean;
  timesheetManager: boolean;
  // v0.0.40 — per-user ISO 27001 workflow flags. Same toggle-pattern
  // as the timesheet flags; default false on insert.
  isIsoMgm: boolean;
  isIsoDpo: boolean;
  // v0.0.40 polish — Knowledge Base opt-in. False on insert; admins flip
  // it on for the agents who should see the KB sidebar entry.
  kbEnabled: boolean;
  // Sidebar feature flag. Default true on insert so brownfield
  // Agent/Admin users keep their search bar on upgrade.
  searchEnabled: boolean;
  // v0.0.42 — per-user opt-in for the agent activity feed. Default
  // false on insert; admins flip it on for the users who should see
  // the dashboard tile + /activity admin page.
  activityFeedEnabled: boolean;
  // v0.0.52 — per-user opt-in for the Assets page (Tactical RMM
  // mirror). Backfilled to true for Agent + Admin on first upgrade;
  // false on new insert until an admin flips it.
  assetsEnabled: boolean;
  // Per-user opt-in for the Adsolut timesheet tab. False on insert;
  // the tab also requires the Adsolut integration to be connected.
  adsolutTimesheetEnabled: boolean;
  // v0.0.56 — per-user opt-in for the back-office Resolved + CWI tabs.
  timesheetBackofficeEnabled: boolean;
  // v0.0.59 — per-user opt-in for the Adsolut Orders feature.
  adsolutOrdersEnabled: boolean;
  // v0.0.69 — per-user opt-in for the Statistics feature. read = view
  // assigned tiles; write = build + assign tiles. Independent flags.
  statisticsRead: boolean;
  statisticsWrite: boolean;
  // v0.0.76 — per-user opt-in for the Contracts page (tile hub).
  contractsEnabled: boolean;
  // Per-user Dashboard tile preferences. Empty array on insert
  // (admins opt the user in to specific tiles).
  dashboardTiles: string[];
};

export type FeatureFlagsUpdate = Partial<{
  timesheetEnabled: boolean;
  timesheetManager: boolean;
  isIsoMgm: boolean;
  isIsoDpo: boolean;
  kbEnabled: boolean;
  searchEnabled: boolean;
  activityFeedEnabled: boolean;
  assetsEnabled: boolean;
  adsolutTimesheetEnabled: boolean;
  timesheetBackofficeEnabled: boolean;
  adsolutOrdersEnabled: boolean;
  statisticsRead: boolean;
  statisticsWrite: boolean;
  contractsEnabled: boolean;
}>;

export type M365PickerUser = {
  oid: string;
  displayName: string | null;
  userPrincipalName: string | null;
  mail: string | null;
  accountEnabled: boolean;
};

export const adminUserApi = {
  list: () => request<UserAdminRow[]>("GET", "/api/admin/users/"),

  /// Graph typeahead. 409 if M365 login is disabled or Graph is not
  /// fully configured — the UI surfaces the server's message verbatim.
  searchM365: (q: string, limit = 20) => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    params.set("limit", String(limit));
    return request<M365PickerUser[]>("GET", `/api/admin/users/m365/search?${params.toString()}`);
  },

  addFromM365: (oid: string, role: "Agent" | "Admin") =>
    request<UserAdminRow>("POST", "/api/admin/users/m365", { oid, role }),

  addLocal: (email: string, password: string, role: "Agent" | "Admin") =>
    request<UserAdminRow>("POST", "/api/admin/users/local", { email, password, role }),

  upgradeToM365: (userId: string, oid: string) =>
    request<UserAdminRow>("POST", `/api/admin/users/${userId}/upgrade-to-m365`, { oid }),

  updateRole: (userId: string, role: "Agent" | "Admin") =>
    request<UserAdminRow>("PUT", `/api/admin/users/${userId}/role`, { role }),

  activate: (userId: string) =>
    request<UserAdminRow>("POST", `/api/admin/users/${userId}/activate`, {}),

  deactivate: (userId: string) =>
    request<UserAdminRow>("POST", `/api/admin/users/${userId}/deactivate`, {}),

  remove: (userId: string) =>
    request<void>("DELETE", `/api/admin/users/${userId}`),

  setTimesheetFlags: (
    userId: string,
    enabled: boolean,
    manager: boolean,
  ) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/timesheet-flags`,
      { enabled, manager },
    ),

  // v0.0.40 — ISO 27001 per-user role flags.
  setIsoFlags: (userId: string, mgm: boolean, dpo: boolean) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/iso-flags`,
      { mgm, dpo },
    ),

  // v0.0.40 polish — Knowledge Base per-user opt-in.
  setKbFlag: (userId: string, enabled: boolean) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/kb-flag`,
      { enabled },
    ),

  // Consolidated partial-update across every per-user feature flag.
  // Only the fields present in `update` are written; the rest stay as
  // they are. One transaction server-side, one audit row per call.
  setFeatureFlags: (userId: string, update: FeatureFlagsUpdate) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/feature-flags`,
      update,
    ),

  // Replace the full set of enabled Dashboard tile-ids for one user.
  // Unknown ids cause a 400; sending an empty array clears the set.
  setDashboardTiles: (userId: string, tileIds: string[]) =>
    request<UserAdminRow>(
      "PUT",
      `/api/admin/users/${userId}/dashboard-tiles`,
      { tileIds },
    ),

  /// v0.0.35-E — per-user Timesheet preference overrides. Reads + writes
  /// the four nullable columns on `users` (day start, day target, week
  /// target, work-days). Each field is independently nullable; a null
  /// means "fall back to the global default".
  getTimesheetPreferences: (userId: string) =>
    request<TimesheetUserPreferencesResponse>(
      "GET",
      `/api/admin/users/${userId}/timesheet-preferences`,
    ),

  setTimesheetPreferences: (
    userId: string,
    body: TimesheetOverrideBody,
  ) =>
    request<TimesheetOverridePayload>(
      "PUT",
      `/api/admin/users/${userId}/timesheet-preferences`,
      body,
    ),
};

export type TimesheetOverridePayload = {
  dayStartMinutes: number | null;
  targetMinutesPerDay: number | null;
  targetMinutesPerWeek: number | null;
  workDays: number[] | null;
  /// v0.0.36 — daily ceiling on absence-task minutes before the ISO-week
  /// is flagged 'target not met' on Tab 3. null = fall back to the global
  /// default (Settings → Timesheet → Max absence per day).
  maxAbsenceMinutesPerDay: number | null;
  /// v0.0.36 — office-hour window used by Tab 1 to flag row-to-row gaps
  /// and overlaps. null = fall back to global.
  officeStartMinutes: number | null;
  officeEndMinutes: number | null;
};

export type TimesheetUserPreferencesResponse = {
  effective: {
    dayStartMinutes: number;
    targetMinutesPerDay: number;
    targetMinutesPerWeek: number;
    workDays: number[];
    maxAbsenceMinutesPerDay: number;
    officeStartMinutes: number;
    officeEndMinutes: number;
  };
  override: TimesheetOverridePayload;
};

export type TimesheetOverrideBody = TimesheetOverridePayload;

/// Row shape returned by the call-popup's phone-keyed lookup. Adds the
/// contact's "best" company link (primary → secondary → supplier priority)
/// on top of the flat Contact fields so the popup can render the linked
/// company without a second round-trip. `linkedCompany*` are all null when
/// the contact has no links at all.
export type ContactPhoneLookupItem = Contact & {
  linkedCompanyId: string | null;
  linkedCompanyName: string | null;
  linkedCompanyRole: ContactCompanyRole | null;
};

export type ContactPhoneLookupResponse = {
  /// Server-normalised E.164 form of the supplied phone. `null` when the
  /// input could not be parsed against the install's default country —
  /// the popup uses that as the "unknown caller" signal.
  phoneE164: string | null;
  items: ContactPhoneLookupItem[];
};

export const contactApi = {
  list: (search?: string, companyId?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (companyId) params.set("companyId", companyId);
    const qs = params.toString();
    return request<Contact[]>("GET", `/api/contacts${qs ? `?${qs}` : ""}`);
  },
  /// v0.0.34 — backs the Telavox call-popup. Server normalises the raw
  /// CAPI `From` number to E.164 using the install-wide DefaultCountryCode
  /// and matches against `phone_e164` / `mobile_phone_e164`.
  lookupByPhone: (phone: string, limit = 10) => {
    const params = new URLSearchParams({ phone, limit: String(limit) });
    return request<ContactPhoneLookupResponse>(
      "GET",
      `/api/contacts/lookup-by-phone?${params.toString()}`,
    );
  },
  browse: (query: ContactBrowseQuery = {}) => {
    const params = new URLSearchParams();
    if (query.search) params.set("search", query.search);
    if (query.companyId) params.set("companyId", query.companyId);
    if (query.role) params.set("role", query.role);
    if (query.includeInactive) params.set("includeInactive", "true");
    if (query.sort) params.set("sort", query.sort);
    if (query.page) params.set("page", String(query.page));
    if (query.pageSize) params.set("pageSize", String(query.pageSize));
    const qs = params.toString();
    return request<ContactOverviewPage>("GET", `/api/contacts/browse${qs ? `?${qs}` : ""}`);
  },
  audit: (id: string, cursor?: number, limit?: number) => {
    const params = new URLSearchParams();
    if (cursor) params.set("cursor", String(cursor));
    if (limit) params.set("limit", String(limit));
    const qs = params.toString();
    return request<ContactAuditPage>("GET", `/api/contacts/${id}/audit${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<Contact>("GET", `/api/contacts/${id}`),
  listCompanies: (id: string) =>
    request<ContactCompanyOption[]>("GET", `/api/contacts/${id}/companies`),
  create: (input: ContactInput) =>
    request<Contact>("POST", "/api/contacts", input),
  update: (id: string, input: ContactInput) =>
    request<Contact>("PUT", `/api/contacts/${id}`, input),
};

export const companyApi = {
  list: (search?: string, includeInactive = false) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (includeInactive) params.set("includeInactive", "true");
    const qs = params.toString();
    return request<Company[]>("GET", `/api/companies${qs ? `?${qs}` : ""}`);
  },
  get: (id: string) => request<CompanyDetail>("GET", `/api/companies/${id}`),
  picker: (search?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    const qs = params.toString();
    return request<CompanyPickerItem[]>("GET", `/api/companies/picker${qs ? `?${qs}` : ""}`);
  },
  create: (input: CompanyInput) => request<Company>("POST", "/api/companies", input),
  update: (id: string, input: CompanyInput) =>
    request<Company>("PUT", `/api/companies/${id}`, input),
  remove: (id: string) => request<void>("DELETE", `/api/companies/${id}`),
  listContacts: (id: string, search?: string) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    const qs = params.toString();
    return request<Contact[]>("GET", `/api/companies/${id}/contacts${qs ? `?${qs}` : ""}`);
  },
  links: (id: string) =>
    request<ContactCompanyLink[]>("GET", `/api/companies/${id}/links`),
  linkContact: (companyId: string, contactId: string, role?: ContactCompanyRole) =>
    request<void>(
      "POST",
      `/api/companies/${companyId}/contacts/${contactId}`,
      role ? { role } : undefined,
    ),
  unlinkContact: (companyId: string, contactId: string) =>
    request<void>("DELETE", `/api/companies/${companyId}/contacts/${contactId}`),
  addDomain: (id: string, domain: string) =>
    request<CompanyDomain>("POST", `/api/companies/${id}/domains`, { domain }),
  removeDomain: (id: string, domainId: string) =>
    request<void>("DELETE", `/api/companies/${id}/domains/${domainId}`),
};
