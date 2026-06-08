import { useState, useEffect, useRef, type ReactNode } from "react";
import { useForm, Controller, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { toast } from "sonner";
import { Drawer } from "vaul";
import { Building2, Loader2, Pencil } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

import { RichTextEditor } from "@/components/RichTextEditor";
import { ContactPicker } from "@/components/ContactPicker";
import { AgentPicker } from "@/components/AgentPicker";
import { CompanyAlertDialog } from "@/components/CompanyAlertDialog";
import { TaxonomySelect } from "@/components/TaxonomySelect";
import { PendingTillField } from "@/components/PendingTillField";
import { TicketCompanyAssignmentDialog } from "@/components/TicketCompanyAssignmentDialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { agentQueueApi, taxonomyApi } from "@/lib/api";
import {
  ticketApi,
  type CompanyAlert,
  type ContactCompanyRole,
  type TicketAttachmentMeta,
} from "@/lib/ticket-api";
import { composeTemplatesApi } from "@/lib/composeTemplates-api";
import { ticketTemplatesApi, type TicketTemplate } from "@/lib/ticketTemplates-api";
import {
  substituteComposeTokens,
  substituteComposeTokensPlain,
} from "@/lib/composeTokens";
import { cn } from "@/lib/utils";

const createTicketSchema = z.object({
  subject: z.string().min(3, "Subject must be at least 3 characters"),
  bodyHtml: z.string().optional(),
  requesterContactId: z.string().uuid("Select a requester"),
  queueId: z.string().uuid(),
  statusId: z.string().uuid(),
  priorityId: z.string().uuid(),
  categoryId: z.string().uuid().optional().or(z.literal("")),
  assigneeUserId: z.string().uuid().optional().or(z.literal("")).or(z.null()),
  // v0.0.37 — optional UTC ISO string. Only honoured when the
  // selected status's state_category is "Pending" (the backend
  // ignores it otherwise). null/undefined → backend computes the
  // default from settings.
  pendingTillUtc: z.string().datetime().optional().nullable(),
});

type CreateTicketForm = z.infer<typeof createTicketSchema>;

const STALE_TIME = 60_000;

function FormLabel({ children }: { children: ReactNode }) {
  return (
    <label className="text-xs font-medium text-muted-foreground uppercase tracking-wider mb-1.5 block">
      {children}
    </label>
  );
}

function FieldError({ message }: { message?: string }) {
  if (!message) return null;
  return <p className="mt-1 text-xs text-destructive">{message}</p>;
}

// Strip empty wrapper tags the rich-text editor leaves behind so an
// untouched initial-note block isn't sent as a real note. Matches the
// shape ProseMirror serialises for an empty document (a single empty
// paragraph) and trims raw whitespace as well.
function stripEmptyHtml(html: string): string {
  return html
    .replace(/<p>(\s|&nbsp;|<br\s*\/?>)*<\/p>/gi, "")
    .replace(/\s+/g, "")
    .trim();
}

export function NewTicketDrawer({
  children,
  initialContactId,
  initialQueueId,
  parentTicketId,
  initialSubject,
  initialBodyHtml,
  initialStatusId,
  initialPriorityId,
  initialCategoryId,
  initialAssigneeUserId,
  ticketTypeId,
  initialNote,
  open: controlledOpen,
  onOpenChange,
}: {
  /// Trigger element. Optional — omit when the drawer is opened
  /// programmatically via the controlled `open` prop (e.g. after an
  /// intermediate type-selection dialog).
  children?: ReactNode;
  /// When set, the drawer pre-fills the requester field with this contact
  /// id on every open. Used by the Telavox call-popup so an agent who
  /// presses "Create ticket" lands on a half-filled form with the caller
  /// already selected. Each open re-applies the pre-fill so re-using the
  /// trigger after a cancelled ticket doesn't surprise the agent with an
  /// empty requester slot.
  initialContactId?: string;
  /// When set, pre-fills the queue dropdown on open. Used by the "Create
  /// linked ticket" flow so the new ticket starts in the same queue as
  /// its parent — the agent can still change it before saving.
  initialQueueId?: string;
  /// When set, the newly created ticket is immediately linked as a
  /// sub-ticket of this parent. Passed to the backend create call;
  /// validation (cycle, merged-state, queue access) happens server-side.
  parentTicketId?: string;
  /// v0.0.39 — extra prefills driven by the manual-trigger
  /// "Create linked X ticket" flow. Each is optional; null/undefined
  /// values fall back to the existing empty-form defaults. Applied
  /// only on the closed → open transition (the same reset() pass that
  /// seeds requester + queue) so opening, editing, cancelling and
  /// re-opening produces a fresh prefill every time.
  initialSubject?: string;
  initialBodyHtml?: string;
  initialStatusId?: string;
  initialPriorityId?: string;
  initialCategoryId?: string;
  initialAssigneeUserId?: string;
  /// v0.0.39 — caller picks the ticket type (typically from a manual
  /// trigger). Null falls back to 'support' server-side. Not displayed
  /// in the UI because the agent already picked it via the type dialog;
  /// surfaced as a non-editable badge in the drawer header.
  ticketTypeId?: string;
  /// v0.0.39 — when set, the drawer renders an extra "Initial note"
  /// block (rich-text + internal/public toggle) and includes the value
  /// in the create payload. Agent can clear the block to suppress the
  /// note. Templates are server-rendered so the body lands ready-to-go.
  initialNote?: { bodyHtml: string; isInternal: boolean };
  /// Optional controlled open state. When provided, the drawer becomes a
  /// controlled component — the parent owns the boolean and must react to
  /// `onOpenChange`. Used by flows that need to open the drawer from
  /// outside the trigger pattern (e.g. after a confirmation/selection
  /// dialog). Leave undefined for the default self-managed behaviour.
  open?: boolean;
  /// Fires when the drawer transitions between open and closed. Used both
  /// for controlled mode (parent must mirror the value into `open`) and
  /// for legacy uncontrolled callers that need to keep a transient
  /// surface alive until the drawer has fully closed.
  onOpenChange?: (open: boolean) => void;
}) {
  const [internalOpen, setInternalOpen] = useState(false);
  const isControlled = controlledOpen !== undefined;
  const open = isControlled ? controlledOpen : internalOpen;
  const setOpen = (value: boolean) => {
    if (!isControlled) setInternalOpen(value);
    onOpenChange?.(value);
  };
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const { data: queues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
  });

  const { data: priorities } = useQuery({
    queryKey: ["taxonomy", "priorities"],
    queryFn: taxonomyApi.priorities.list,
    staleTime: STALE_TIME,
  });

  const { data: statuses } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: taxonomyApi.statuses.list,
    staleTime: STALE_TIME,
  });

  const { data: categories } = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: taxonomyApi.categories.list,
    staleTime: STALE_TIME,
  });

  const defaultPriorityId =
    priorities?.find((p) => p.isDefault && p.isActive)?.id ??
    priorities?.find((p) => p.isActive)?.id ??
    "";

  const defaultStatusId =
    statuses?.find((s) => s.isDefault && s.stateCategory === "New")?.id ??
    statuses?.find((s) => s.isDefault)?.id ??
    statuses?.find((s) => s.stateCategory === "New")?.id ??
    "";

  const {
    register,
    handleSubmit,
    control,
    reset,
    setValue,
    formState: { errors },
  } = useForm<CreateTicketForm>({
    resolver: zodResolver(createTicketSchema),
    defaultValues: {
      subject: "",
      bodyHtml: "",
      requesterContactId: "",
      queueId: "",
      statusId: "",
      priorityId: "",
      categoryId: "",
      assigneeUserId: null,
      pendingTillUtc: null,
    },
  });

  // Drives the conditional "Pending till" field in the right column —
  // we re-render whenever statusId changes so the field appears/hides
  // in sync with the selected state_category.
  const watchedStatusId = useWatch({ control, name: "statusId" });
  const watchedQueueId = useWatch({ control, name: "queueId" });
  const watchedRequesterContactId = useWatch({
    control,
    name: "requesterContactId",
  });

  // Resolve {{contact.*}} tokens for the picked requester so the ::
  // template picker substitutes them on insert. No ticket exists yet,
  // so ticket-level tokens (number / subject) stay empty and the raw
  // placeholder remains visible — the user can still edit it once the
  // ticket is created.
  const tokensQ = useQuery({
    queryKey: [
      "compose-templates",
      "resolve",
      { contactId: watchedRequesterContactId || null },
    ],
    queryFn: () =>
      composeTemplatesApi.resolveTokens({
        contactId: watchedRequesterContactId || null,
      }),
    staleTime: 30_000,
    enabled: !!watchedRequesterContactId,
  });
  const composeTokens = tokensQ.data?.tokens;

  // Ticket templates the agent can apply to pre-fill this form. Fetched
  // once the drawer is open; admin-managed, available to every agent.
  const { data: ticketTemplates } = useQuery({
    queryKey: ["ticket-templates", "usable"],
    queryFn: ticketTemplatesApi.usable,
    staleTime: 60_000,
    enabled: open,
  });

  // Which template the agent applied (for the picker label) and the type it
  // set. Type has no field in this form, so a template-supplied type rides
  // in local state and falls back to the `ticketTypeId` prop when unset.
  const [appliedTemplateId, setAppliedTemplateId] = useState<string>("");
  const [templateTicketTypeId, setTemplateTicketTypeId] = useState<string | null>(null);

  // Track the previous `open` so we can do a single full-reset on the
  // false → true transition. setValue inside a useEffect was unreliable
  // here because Vaul's portal mounts the Controller after the effect
  // runs the first time, and Controller's first render captured the
  // pre-setValue value. reset() is the idiomatic RHF way to seed the
  // entire form when "opening" — a Controller mounting later picks up
  // the freshly-written _formValues. Stays a ref (not a state) so we
  // don't double-render on the transition.
  // v0.0.39 — local state for the optional initial-note block. RHF's
  // form schema stays focused on ticket-shape fields; the note has its
  // own simpler shape and the toggle is independent. Seeded on the
  // open transition alongside the form reset and cleared on close.
  const [noteState, setNoteState] = useState<{ bodyHtml: string; isInternal: boolean } | null>(null);

  // v0.0.73 — inline images in the opening note. No ticket id exists while
  // the drawer is open, and the attachment upload endpoint is ticket-scoped,
  // so a pasted/dropped image can't be uploaded yet. Instead we keep the
  // File and show a local blob: preview; on submit the images are uploaded
  // against the freshly-created ticket and their blob: srcs swapped for the
  // real attachment URLs. Keyed by the object URL embedded in the note HTML.
  const noteImageFilesRef = useRef<Map<string, File>>(new Map());

  // Revoke every outstanding blob: preview and forget the Files. Called on
  // close and after a successful submit so previews don't leak across opens.
  function revokeNoteImages() {
    for (const url of noteImageFilesRef.current.keys()) URL.revokeObjectURL(url);
    noteImageFilesRef.current.clear();
  }

  // Upload handler wired into the note editor. Only images embed inline;
  // other files would need an attachment tray the create form doesn't have,
  // so they're refused with a hint to attach them once the ticket exists.
  async function handleNoteImageUpload(file: File): Promise<TicketAttachmentMeta | null> {
    if (!file.type.startsWith("image/")) {
      toast.warning(
        "Only images can be embedded here. Attach other files once the ticket is created.",
      );
      return null;
    }
    const objectUrl = URL.createObjectURL(file);
    noteImageFilesRef.current.set(objectUrl, file);
    return {
      id: `pending:${objectUrl}`,
      url: objectUrl,
      mimeType: file.type,
      size: file.size,
      filename: file.name,
    };
  }

  // Pending images whose blob: URL still appears in the note HTML (an image
  // pasted and then deleted is skipped). Returned in no particular order.
  function collectPendingNoteImages(html: string): { objectUrl: string; file: File }[] {
    const out: { objectUrl: string; file: File }[] = [];
    for (const [objectUrl, file] of noteImageFilesRef.current) {
      if (html.includes(objectUrl)) out.push({ objectUrl, file });
    }
    return out;
  }

  // Second-step opening note: upload each embedded image against the new
  // ticket, swap its blob: src for the returned (session-authenticated)
  // attachment URL, then post the note. The uploaded ids ride along in
  // attachmentIds so the server links them onto the event.
  async function uploadNoteImagesAndPost(
    ticketId: string,
    note: { bodyHtml: string; isInternal: boolean },
    images: { objectUrl: string; file: File }[],
  ): Promise<void> {
    let html = note.bodyHtml;
    const attachmentIds: string[] = [];
    for (const { objectUrl, file } of images) {
      const meta = await ticketApi.uploadAttachment(ticketId, file);
      html = html.split(objectUrl).join(meta.url);
      attachmentIds.push(meta.id);
    }
    await ticketApi.addEvent(ticketId, {
      eventType: note.isInternal ? "Note" : "Comment",
      bodyHtml: html,
      isInternal: note.isInternal,
      attachmentIds,
    });
  }

  // v0.0.51 — agent-supplied company linked to the new ticket. The
  // picker auto-opens whenever the requester changes (including the
  // initial open if a contact was pre-filled). Empty selection ↔ no
  // explicit choice; backend then falls back to the cascade. Tracked
  // by a ref so a rerender that doesn't change the requester doesn't
  // re-open the dialog or wipe a confirmed choice.
  const [companyDialogOpen, setCompanyDialogOpen] = useState(false);
  const [selectedCompanyId, setSelectedCompanyId] = useState<string | null>(null);
  const [selectedCompanyName, setSelectedCompanyName] = useState<string | null>(null);
  // Role for a brand-new contact_companies link. Null when the agent
  // picked a company that's already on the contact's link list (no
  // new row needed) OR cleared the choice.
  const [selectedNewLinkRole, setSelectedNewLinkRole] = useState<ContactCompanyRole | null>(null);
  const lastProcessedRequesterRef = useRef<string | null>(null);

  const previousOpenRef = useRef(false);
  useEffect(() => {
    const wasOpen = previousOpenRef.current;
    previousOpenRef.current = open;
    if (open && !wasOpen) {
      reset({
        subject: initialSubject ?? "",
        bodyHtml: initialBodyHtml ?? "",
        requesterContactId: initialContactId ?? "",
        queueId: initialQueueId ?? "",
        statusId: initialStatusId || defaultStatusId || "",
        priorityId: initialPriorityId || defaultPriorityId || "",
        categoryId: initialCategoryId ?? "",
        assigneeUserId: initialAssigneeUserId ?? null,
        pendingTillUtc: null,
      });
      // The opening-note block is always present now (v0.0.73). A manual
      // trigger / template can seed it; otherwise it starts empty and
      // internal. An empty note is stripped before submit, so a blank
      // block costs nothing.
      setNoteState(initialNote ?? { bodyHtml: "", isInternal: true });
      // Reset the template picker so a re-open starts unselected.
      setAppliedTemplateId("");
      setTemplateTicketTypeId(null);
    }
    if (!open && wasOpen) {
      // Drop the note on close so re-opening from a different preset
      // doesn't inherit a stale body. The form's reset() is handled
      // by handleClose / the mutation onSuccess.
      setNoteState(null);
      // Release any local image previews still held for a note that was
      // never submitted, so the blob: URLs don't leak across opens.
      revokeNoteImages();
      // v0.0.51 — close the company picker and drop any agent-picked
      // company so re-opening for a different requester starts clean.
      setCompanyDialogOpen(false);
      setSelectedCompanyId(null);
      setSelectedCompanyName(null);
      setSelectedNewLinkRole(null);
      lastProcessedRequesterRef.current = null;
    }
  });

  // v0.0.51 — open the company-picker when the requester changes to a
  // new non-empty value. Triggers on first open with an `initialContactId`
  // and on every subsequent edit through the ContactPicker. Clearing the
  // requester wipes the prior selection so it can't leak across contacts.
  useEffect(() => {
    if (!open) return;
    const requesterId = watchedRequesterContactId || null;
    if (requesterId === lastProcessedRequesterRef.current) return;
    lastProcessedRequesterRef.current = requesterId;
    setSelectedCompanyId(null);
    setSelectedCompanyName(null);
    setSelectedNewLinkRole(null);
    setCompanyDialogOpen(!!requesterId);
  }, [open, watchedRequesterContactId]);

  // Taxonomy defaults (priority / status) can arrive AFTER the drawer is
  // already open if the agent clicked the trigger before the first
  // taxonomy fetch resolved. Patch them in via setValue so we don't
  // clobber a subject/body the user has already typed.
  useEffect(() => {
    if (!open) return;
    if (defaultPriorityId) setValue("priorityId", defaultPriorityId);
    if (defaultStatusId) setValue("statusId", defaultStatusId);
  }, [open, defaultPriorityId, defaultStatusId, setValue]);

  // v0.0.40 polish — when the chosen queue's allowed-list excludes the
  // current statusId selection, swap to the queue's default (or the
  // first allowed status as fallback). Mirrors the server's auto-flip
  // on UpdateFieldsAsync — keeps the form preview consistent with what
  // POST would land on.
  const watchedStatusInForm = useWatch({ control, name: "statusId" });
  useEffect(() => {
    if (!open) return;
    const q = (queues ?? []).find((x) => x.id === watchedQueueId);
    if (!q) return;
    const allowed = q.allowedStatusIds ?? [];
    if (allowed.length === 0) return;
    if (watchedStatusInForm && allowed.includes(watchedStatusInForm)) return;
    const next = q.defaultStatusId && allowed.includes(q.defaultStatusId)
      ? q.defaultStatusId
      : allowed[0];
    if (next) setValue("statusId", next);
  }, [open, watchedQueueId, watchedStatusInForm, queues, setValue]);

  const [postCreateAlert, setPostCreateAlert] = useState<CompanyAlert | null>(null);

  const { mutate: submitTicket, isPending } = useMutation({
    mutationFn: async (data: CreateTicketForm) => {
      // Only send pendingTillUtc when the chosen status is Pending; the
      // backend ignores it for other statuses but sending it would still
      // leak through the audit payload and create noise.
      const status = statuses?.find((s) => s.id === data.statusId);
      const pendingTillUtc =
        status?.stateCategory === "Pending" && data.pendingTillUtc
          ? data.pendingTillUtc
          : undefined;

      // Only treat the note as real when the agent populated it. An empty
      // body or a bare empty <p></p> from the editor counts as "no note".
      const noteHasContent =
        noteState !== null && stripEmptyHtml(noteState.bodyHtml).length > 0;
      // Images embedded in the note still point at local blob: previews —
      // they can only be uploaded once the ticket id exists. A note with
      // such images is therefore posted as a second step after creation;
      // a note without them rides along inline in the create call as before.
      const pendingImages =
        noteHasContent && noteState ? collectPendingNoteImages(noteState.bodyHtml) : [];
      const inlineNote =
        noteHasContent && noteState && pendingImages.length === 0
          ? { bodyHtml: noteState.bodyHtml, isInternal: noteState.isInternal }
          : undefined;

      const response = await ticketApi.create({
        subject: data.subject,
        bodyHtml: data.bodyHtml || undefined,
        requesterContactId: data.requesterContactId,
        queueId: data.queueId,
        statusId: data.statusId,
        priorityId: data.priorityId,
        categoryId: data.categoryId || undefined,
        assigneeUserId: data.assigneeUserId || undefined,
        source: "Web",
        pendingTillUtc,
        parentTicketId,
        // A template-supplied type wins over the prop fallback; both null
        // lets the backend default to 'support'.
        ticketTypeId: templateTicketTypeId ?? ticketTypeId,
        initialNote: inlineNote,
        // v0.0.51 — agent's explicit company pick from the create-time
        // popup. Omitted when the agent cancelled the popup; the backend
        // then runs its cascade (same as mail-intake). newLinkRole rides
        // along when the picked company wasn't yet on the contact's
        // link list, so the backend can upsert the row in the same call.
        companyId: selectedCompanyId ?? undefined,
        newLinkRole: selectedCompanyId && selectedNewLinkRole ? selectedNewLinkRole : undefined,
      });

      // Two-step note for the image case. A failure here must not lose the
      // already-created ticket: warn and resolve so we still navigate to it.
      if (noteHasContent && noteState && pendingImages.length > 0) {
        try {
          await uploadNoteImagesAndPost(response.ticket.id, noteState, pendingImages);
        } catch {
          toast.warning(
            "Ticket created, but the opening note couldn't be saved. Add it from the ticket.",
          );
        }
      }
      return response;
    },
    onSuccess: (response) => {
      toast.success("Ticket created");
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      revokeNoteImages();
      setOpen(false);
      reset();
      if (response.showAlertOnCreate && response.companyAlert) {
        setPostCreateAlert(response.companyAlert);
      }
      navigate({ to: "/tickets/$ticketId", params: { ticketId: response.ticket.id } });
    },
    onError: () => {
      toast.error("Failed to create ticket");
    },
  });

  function onSubmit(data: CreateTicketForm) {
    submitTicket(data);
  }

  function handleClose() {
    setOpen(false);
    reset();
  }

  // Apply a ticket template's pre-fills onto the form. Only the fields the
  // template actually sets are written; everything else keeps the agent's
  // current choice. Tokens in subject/body/initial-note resolve against the
  // currently-selected requester + company (empty when none picked yet, so
  // the raw {{placeholder}} stays visible). Re-applying overwrites prior
  // template pre-fills, which is the intent of an explicit pick.
  function applyTemplate(template: TicketTemplate) {
    setAppliedTemplateId(template.id);
    if (template.subject) {
      setValue("subject", substituteComposeTokensPlain(template.subject, composeTokens));
    }
    if (template.bodyHtml) {
      setValue("bodyHtml", substituteComposeTokens(template.bodyHtml, composeTokens));
    }
    if (template.queueId) setValue("queueId", template.queueId);
    if (template.statusId) setValue("statusId", template.statusId);
    if (template.priorityId) setValue("priorityId", template.priorityId);
    if (template.categoryId) setValue("categoryId", template.categoryId);
    if (template.assigneeUserId) setValue("assigneeUserId", template.assigneeUserId);
    // null clears a previously-applied type so the prop fallback returns.
    setTemplateTicketTypeId(template.ticketTypeId ?? null);
    if (stripEmptyHtml(template.initialNoteHtml).length > 0) {
      setNoteState({
        bodyHtml: substituteComposeTokens(template.initialNoteHtml, composeTokens),
        isInternal: template.initialNoteInternal,
      });
    }
  }

  const activeQueues = (queues ?? []).filter((q) => q.isActive);
  const activePriorities = (priorities ?? [])
    .filter((p) => p.isActive)
    .sort((a, b) => a.sortOrder - b.sortOrder);
  // v0.0.40 polish — filter statuses against the currently-selected
  // queue's allowed-list. Empty list = no scoping, all active
  // statuses available (backward-compat with queues created before
  // this column existed).
  const selectedQueue = activeQueues.find((q) => q.id === watchedQueueId);
  const queueAllowedStatusIds = selectedQueue?.allowedStatusIds ?? [];
  const activeStatuses = (statuses ?? [])
    .filter((s) => s.isActive)
    .filter((s) => queueAllowedStatusIds.length === 0 || queueAllowedStatusIds.includes(s.id));
  const activeCategories = (categories ?? []).filter((c) => c.isActive);

  const queueOptions = activeQueues.map((q) => ({
    id: q.id,
    name: q.name,
    color: q.color,
  }));

  const priorityOptions = activePriorities.map((p) => ({
    id: p.id,
    name: p.name,
    color: p.color,
  }));

  const statusOptions = activeStatuses.map((s) => ({
    id: s.id,
    name: s.name,
    color: s.color,
    badge: s.stateCategory,
  }));

  const categoryOptions = activeCategories.map((c) => ({
    id: c.id,
    name: c.name,
    color: "#6b7280",
  }));

  const taxonomyReady =
    queues !== undefined &&
    priorities !== undefined &&
    statuses !== undefined &&
    categories !== undefined;

  return (
    <>
    {postCreateAlert && (
      <CompanyAlertDialog
        alert={postCreateAlert}
        open={!!postCreateAlert}
        onClose={() => setPostCreateAlert(null)}
      />
    )}
    <Drawer.Root open={open} onOpenChange={setOpen}>
      {children && <Drawer.Trigger asChild>{children}</Drawer.Trigger>}
      <Drawer.Portal>
        <Drawer.Overlay className="fixed inset-0 z-50 bg-black/60 backdrop-blur-sm" />
        <Drawer.Content className="fixed inset-x-0 bottom-0 z-50 mx-auto flex max-h-[90vh] max-w-5xl flex-col rounded-t-[var(--radius)] border border-glass bg-background/90 backdrop-blur-xl">
          <Drawer.Title className="sr-only">New ticket</Drawer.Title>
          <Drawer.Description className="sr-only">
            Create a new support ticket.
          </Drawer.Description>

          <div className="mx-auto mt-3 h-1 w-10 shrink-0 rounded-full bg-glass-strong" aria-hidden />

          <div className="flex items-center justify-between px-6 py-4 border-b border-glass shrink-0">
            <h2 className="font-display text-display-sm font-semibold">New ticket</h2>
            {!taxonomyReady && (
              <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
                <Loader2 className="h-3 w-3 animate-spin" />
                Loading…
              </span>
            )}
          </div>

          <form
            onSubmit={handleSubmit(onSubmit)}
            className="flex min-h-0 flex-1 flex-col"
          >
            <div className="flex min-h-0 flex-1 overflow-y-auto">
              <div className="flex flex-1 flex-col gap-0 min-[560px]:flex-row">
                {/* Left column — Subject + Body */}
                <div className="flex flex-1 flex-col gap-4 p-6 min-[560px]:border-r min-[560px]:border-glass">
                  {(ticketTemplates?.length ?? 0) > 0 && (
                    <div>
                      <FormLabel>Template</FormLabel>
                      <Select
                        value={appliedTemplateId || undefined}
                        onValueChange={(id) => {
                          const t = ticketTemplates?.find((x) => x.id === id);
                          if (t) applyTemplate(t);
                        }}
                      >
                        <SelectTrigger className="border-glass bg-glass focus:border-glass-strong focus:bg-glass-strong">
                          <SelectValue placeholder="Apply a template…" />
                        </SelectTrigger>
                        <SelectContent>
                          {(ticketTemplates ?? []).map((t) => (
                            <SelectItem key={t.id} value={t.id}>
                              {t.name}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                      <p className="mt-1 text-[10px] text-muted-foreground/70">
                        Pick a requester first so variables fill in. Applying a
                        template overwrites the fields it sets.
                      </p>
                    </div>
                  )}

                  <div>
                    <FormLabel>Subject *</FormLabel>
                    <Input
                      {...register("subject")}
                      placeholder="Brief summary of the issue"
                      className="border-glass bg-glass focus:border-glass-strong focus:bg-glass-strong"
                    />
                    <FieldError message={errors.subject?.message} />
                  </div>

                  <div className="flex-1">
                    <FormLabel>Description</FormLabel>
                    <Controller
                      name="bodyHtml"
                      control={control}
                      render={({ field }) => (
                        <RichTextEditor
                          content={field.value ?? ""}
                          onChange={field.onChange}
                          placeholder="Describe the issue. Type :: to insert a template…"
                          minHeight="180px"
                          composeTokens={composeTokens}
                          onIntakeQuery={async (q) => {
                            // New-Ticket drawer: pre-creation, so the
                            // intake-form chip flow doesn't apply (no
                            // ticket id yet). Only compose templates
                            // surface here, scoped to the chosen queue.
                            const list = await composeTemplatesApi.usable(
                              watchedQueueId || null,
                            );
                            const needle = q.trim().toLowerCase();
                            const filtered = needle
                              ? list.filter(
                                  (t) =>
                                    t.name.toLowerCase().includes(needle) ||
                                    (t.description ?? "")
                                      .toLowerCase()
                                      .includes(needle),
                                )
                              : list;
                            return filtered.slice(0, 12).map((t) => ({
                              id: t.id,
                              name: t.name,
                              description: t.description,
                              kind: "template" as const,
                              bodyHtml: t.bodyHtml,
                            }));
                          }}
                        />
                      )}
                    />
                  </div>

                  {/* v0.0.73 — opening-note block, always shown. Agent can
                      edit the body, flip internal/public, and paste / drag /
                      attach images that embed inline. A manual trigger or
                      template can pre-fill it. An empty note is dropped on
                      submit, so leaving it blank is fine. */}
                  {noteState !== null && (
                    <div className="rounded-lg border border-glass-strong bg-glass p-4">
                      <div className="mb-2 flex items-center justify-between">
                        <FormLabel>Initial note</FormLabel>
                        <div className="flex items-center gap-1 rounded-md border border-glass-strong bg-glass p-0.5 text-[11px]">
                          <button
                            type="button"
                            onClick={() => setNoteState((s) => s ? { ...s, isInternal: true } : s)}
                            className={cn(
                              "rounded px-2 py-0.5 transition",
                              noteState.isInternal
                                ? "bg-amber-500/20 text-amber-200"
                                : "text-muted-foreground hover:text-foreground",
                            )}
                          >
                            Internal
                          </button>
                          <button
                            type="button"
                            onClick={() => setNoteState((s) => s ? { ...s, isInternal: false } : s)}
                            className={cn(
                              "rounded px-2 py-0.5 transition",
                              !noteState.isInternal
                                ? "bg-emerald-500/20 text-emerald-200"
                                : "text-muted-foreground hover:text-foreground",
                            )}
                          >
                            Public
                          </button>
                        </div>
                      </div>
                      <RichTextEditor
                        content={noteState.bodyHtml}
                        onChange={(html) => setNoteState((s) => s ? { ...s, bodyHtml: html } : s)}
                        placeholder="Add an opening note. Paste or drag images to embed them inline…"
                        minHeight="120px"
                        composeTokens={composeTokens}
                        onUploadFile={handleNoteImageUpload}
                      />
                    </div>
                  )}
                </div>

                {/* Right column — metadata */}
                <div className="flex w-full flex-col gap-4 p-6 min-[560px]:w-[280px] min-[560px]:shrink-0">
                  <div>
                    <FormLabel>Requester *</FormLabel>
                    <Controller
                      name="requesterContactId"
                      control={control}
                      render={({ field }) => (
                        <ContactPicker
                          value={field.value || null}
                          onChange={field.onChange}
                          placeholder="Select a contact…"
                        />
                      )}
                    />
                    <FieldError message={errors.requesterContactId?.message} />
                  </div>

                  {/* v0.0.51 — agent-picked company for this ticket.
                      Visible once a requester is selected. Opens the
                      same dialog the side panel uses, in 'create' mode
                      so the choice flows back into the form instead of
                      committing immediately. */}
                  {watchedRequesterContactId && (
                    <div>
                      <FormLabel>Company</FormLabel>
                      <button
                        type="button"
                        onClick={() => setCompanyDialogOpen(true)}
                        className="flex w-full items-center gap-2 rounded-md border border-glass bg-glass px-3 py-2 text-left text-sm transition-colors hover:bg-glass-hover focus:border-glass-strong focus:bg-glass-strong"
                      >
                        <Building2 className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                        <span className={cn("flex-1 truncate", !selectedCompanyName && "text-muted-foreground")}>
                          {selectedCompanyName ?? "No company linked"}
                        </span>
                        <Pencil className="h-3 w-3 shrink-0 text-muted-foreground/70" />
                      </button>
                      {selectedCompanyId && selectedNewLinkRole && (
                        <p className="mt-1 text-[10px] text-muted-foreground/70">
                          Will also link this contact as {selectedNewLinkRole}.
                        </p>
                      )}
                    </div>
                  )}

                  <div>
                    <FormLabel>Status *</FormLabel>
                    <Controller
                      name="statusId"
                      control={control}
                      render={({ field }) => (
                        <TaxonomySelect
                          value={field.value}
                          onChange={field.onChange}
                          options={statusOptions}
                          placeholder="Select status…"
                          disabled={!taxonomyReady}
                        />
                      )}
                    />
                    <FieldError message={errors.statusId?.message} />
                  </div>

                  {(() => {
                    // v0.0.37 — show "Pending till" only when the
                    // selected status sits in the Pending state.
                    // Leaving the field unset is fine: the backend
                    // computes a default (business days + wake-at-local
                    // from settings) on create when this is null.
                    const selectedStatus = statuses?.find(
                      (s) => s.id === watchedStatusId,
                    );
                    if (selectedStatus?.stateCategory !== "Pending") return null;
                    return (
                      <div>
                        <FormLabel>Pending till</FormLabel>
                        <Controller
                          name="pendingTillUtc"
                          control={control}
                          render={({ field }) => (
                            <PendingTillField
                              value={field.value ?? null}
                              onCommit={field.onChange}
                            />
                          )}
                        />
                        <p className="mt-1 text-[10px] text-muted-foreground/70">
                          Leave blank to let the server compute it from your
                          Pending defaults (Settings → Tickets).
                        </p>
                      </div>
                    );
                  })()}

                  <div>
                    <FormLabel>Queue *</FormLabel>
                    <Controller
                      name="queueId"
                      control={control}
                      render={({ field }) => (
                        <TaxonomySelect
                          value={field.value}
                          onChange={field.onChange}
                          options={queueOptions}
                          placeholder="Select queue…"
                          disabled={!taxonomyReady}
                        />
                      )}
                    />
                    <FieldError message={errors.queueId?.message} />
                  </div>

                  <div>
                    <FormLabel>Priority *</FormLabel>
                    <Controller
                      name="priorityId"
                      control={control}
                      render={({ field }) => (
                        <TaxonomySelect
                          value={field.value}
                          onChange={field.onChange}
                          options={priorityOptions}
                          placeholder="Select priority…"
                          disabled={!taxonomyReady}
                        />
                      )}
                    />
                    <FieldError message={errors.priorityId?.message} />
                  </div>

                  <div>
                    <FormLabel>Assignee</FormLabel>
                    <Controller
                      name="assigneeUserId"
                      control={control}
                      render={({ field }) => (
                        <AgentPicker
                          value={field.value ?? null}
                          onChange={field.onChange}
                          placeholder="Unassigned"
                        />
                      )}
                    />
                  </div>

                  <div>
                    <FormLabel>Category</FormLabel>
                    <Controller
                      name="categoryId"
                      control={control}
                      render={({ field }) => (
                        <TaxonomySelect
                          value={field.value ?? ""}
                          onChange={field.onChange}
                          options={categoryOptions}
                          placeholder="Select category…"
                          disabled={!taxonomyReady}
                          allowEmpty
                          emptyLabel="None"
                        />
                      )}
                    />
                  </div>
                </div>
              </div>
            </div>

            {/* Footer */}
            <div className="flex shrink-0 items-center justify-between gap-3 border-t border-glass px-6 py-4">
              <div className="flex items-center gap-2">
                {Object.keys(errors).length > 0 && (
                  <p className="text-xs text-destructive">
                    Please fix the errors above.
                  </p>
                )}
              </div>
              <div className="flex items-center gap-2">
                <Button
                  type="button"
                  variant="ghost"
                  onClick={handleClose}
                  disabled={isPending}
                >
                  Cancel
                </Button>
                <Button
                  type="submit"
                  disabled={isPending || !taxonomyReady}
                  className="bg-gradient-to-r from-accent-purple to-accent-blue text-white hover:opacity-90 transition-opacity border-0"
                >
                  {isPending ? (
                    <>
                      <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />
                      Creating…
                    </>
                  ) : (
                    "Create ticket"
                  )}
                </Button>
              </div>
            </div>
          </form>
        </Drawer.Content>
      </Drawer.Portal>
    </Drawer.Root>
    {/* v0.0.51 — company picker for the create-time flow. Receives the
        agent's choice into local state; the actual write happens with
        POST /api/tickets in onSubmit. Cancelling leaves selection null
        and the backend falls back to the cascade. */}
    {watchedRequesterContactId && (
      <TicketCompanyAssignmentDialog
        open={companyDialogOpen}
        contactId={watchedRequesterContactId}
        mode="create"
        onClose={() => setCompanyDialogOpen(false)}
        onAssigned={() => setCompanyDialogOpen(false)}
        submit={async (companyId, companyName, newLinkRole) => {
          setSelectedCompanyId(companyId);
          setSelectedCompanyName(companyName);
          setSelectedNewLinkRole(newLinkRole);
        }}
      />
    )}
    </>
  );
}
