import * as React from "react";
import { AnimatePresence, motion } from "framer-motion";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { toast } from "sonner";
import { ArrowLeft, Building2, Phone, PhoneOff, Smartphone, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useIncomingCallStore } from "@/stores/useIncomingCallStore";
import { contactApi, type Contact, type ContactPhoneLookupItem } from "@/lib/ticket-api";
import { ApiError } from "@/lib/api";
import { NewTicketDrawer } from "@/shell/NewTicketDrawer";
import { ContactFormDialog } from "@/components/ContactFormDialog";
import { cn } from "@/lib/utils";

/// Treat any value in this set as "no active call". CAPI's
/// OngoingCallDto lineStatus uses "down" for terminated; the uppercase
/// variants are legacy from the v0.0.34 pre-CAPI-rewrite draft. Matching
/// is case-insensitive at the call site.
const TERMINAL_STATES = new Set([
  "down",
  "ended",
  "hangup",
  "terminated",
  "dropped",
]);

function isRinging(state: string): boolean {
  const s = state.toLowerCase();
  return s === "ringing" || s === "ring" || s === "alerting";
}

function isAnswered(state: string): boolean {
  const s = state.toLowerCase();
  return s === "up" || s === "answered" || s === "connected" || s === "active";
}

function displayName(c: ContactPhoneLookupItem): string {
  const full = `${c.firstName ?? ""} ${c.lastName ?? ""}`.trim();
  return full.length > 0 ? full : (c.email || "Contact");
}

const ROLE_LABEL: Record<NonNullable<ContactPhoneLookupItem["linkedCompanyRole"]>, string> = {
  primary: "Primary",
  secondary: "Secondary",
  supplier: "Supplier",
};

/// Shared outlined-glass style used by the three action buttons on the
/// matched-contact branch. `flex-1` + `min-w-0` so the row of three buttons
/// fits a single line inside the 360px popup. `whitespace-nowrap` forces
/// labels on one line — wrapping pushed text outside the fixed h-8 in
/// earlier iterations. `px-1.5` is the tightest padding that still feels
/// like a button.
const CALL_ACTION_BTN =
  "h-8 flex-1 min-w-0 border border-glass-strong bg-glass text-foreground hover:bg-glass-hover hover:border-glass-strong px-1.5 text-[11px] whitespace-nowrap";

/// Glassmorphism corner card surfaced on every TelavoxCall push. Mounts
/// once at AppShell level; reads the latest event from the store and
/// performs a phone-keyed contact-lookup so the agent sees who is
/// calling before they pick up. No close-on-outside-click: a missed
/// call should stay on screen until the agent dismisses or a newer
/// event replaces it.
export function IncomingCallPopup() {
  const current = useIncomingCallStore((s) => s.current);
  const dismiss = useIncomingCallStore((s) => s.dismiss);
  const navigate = useNavigate();

  // Tracks whether the agent kicked off "Create ticket". While the drawer
  // is open we hide the popup visually but keep it (and therefore the
  // drawer's React subtree) mounted — synchronously dismissing the popup
  // in the click handler would unmount the drawer the moment it tries to
  // open. The actual dismiss happens once the drawer closes again, via
  // the drawer's onOpenChange callback.
  const [creatingTicket, setCreatingTicket] = React.useState(false);
  // Same parent-mount problem applies to the create-contact and
  // link-to-existing-contact dialogs on the unknown-number branch — keep
  // the popup alive until those finish.
  const [createContactOpen, setCreateContactOpen] = React.useState(false);
  const [linkPickerOpen, setLinkPickerOpen] = React.useState(false);
  const interacting = creatingTicket || createContactOpen || linkPickerOpen;

  // Auto-dismiss on terminal states. The worker debounces same-state
  // ticks server-side, so an ENDED push is the cleanup signal. We
  // suppress this while the agent is drafting a ticket — a hang-up
  // mid-draft must NOT unmount the popup, because the drawer is a child
  // of this component and would die with it.
  React.useEffect(() => {
    if (!current) return;
    if (interacting) return;
    if (TERMINAL_STATES.has(current.state.toLowerCase())) {
      // Slight delay so the agent has a chance to see "call ended" if
      // they were still looking at the popup. Not configurable yet —
      // a hard 4s is short enough to feel responsive and long enough
      // for a glance.
      const id = window.setTimeout(() => dismiss(current.callId), 4000);
      return () => window.clearTimeout(id);
    }
    return;
  }, [current, dismiss, interacting]);

  const phone = current?.fromNumber ?? "";

  // The lookup is keyed on the raw "From" — the server does its own
  // normalisation, so the popup stays dumb and the canonical form lives
  // in one place. Disabled when there's no phone (anonymous caller).
  // Short staleTime so a contact-edit (phone updated, contact deleted,
  // DefaultCountryCode changed in settings) is reflected on the next
  // incoming call without the previous match leaking through the cache.
  const lookup = useQuery({
    queryKey: ["telavox", "lookup", phone],
    queryFn: () => contactApi.lookupByPhone(phone),
    enabled: !!current && phone.length > 0,
    staleTime: 30_000,
    // The popup is short-lived; one retry on transient blips is plenty.
    retry: 1,
  });

  if (!current) return null;

  const match = lookup.data?.items ?? [];
  const primary = match[0];
  const stateLabel = isRinging(current.state)
    ? "Ringing"
    : isAnswered(current.state)
      ? "In call"
      : current.state;
  const ringing = isRinging(current.state);

  const phoneForLinking = lookup.data?.phoneE164 ?? phone;

  return (
    // mode="wait" so a fast-replaced call (call A ENDED, call B RINGING
    // arrives within the exit animation) doesn't stack two motion.divs
    // for one frame in the bottom-right corner.
    <>
    <AnimatePresence mode="wait">
      <motion.div
        key={current.callId}
        initial={{ opacity: 0, x: 16, y: 16 }}
        // While a child dialog or drawer is open we slide+fade the popup
        // out of view but keep the React tree mounted so that child (a
        // descendant of this component) survives. Visually it feels like
        // the popup makes way for whatever the agent opened.
        animate={{
          opacity: interacting ? 0 : 1,
          x: interacting ? 16 : 0,
          y: interacting ? 16 : 0,
        }}
        exit={{ opacity: 0, x: 16, y: 16 }}
        transition={{ type: "spring", stiffness: 280, damping: 26 }}
        className={cn(
          "fixed bottom-6 right-6 z-[60] w-[360px] overflow-hidden",
          "rounded-2xl border border-glass-strong shadow-2xl",
          "bg-popover/95 backdrop-blur-xl",
          interacting ? "pointer-events-none" : "pointer-events-auto",
        )}
        role="dialog"
        aria-label="Incoming call"
      >
        {/* Glow strip — keeps the card from feeling generic-AI-default. */}
        <div
          className={cn(
            "h-1 w-full",
            ringing
              ? "animate-pulse bg-gradient-to-r from-violet-500 via-indigo-500 to-blue-500"
              : "bg-gradient-to-r from-emerald-500 to-teal-500",
          )}
        />

        <div className="flex items-start justify-between gap-3 p-4 pb-3">
          <div className="flex min-w-0 items-start gap-3">
            <div
              className={cn(
                "flex h-10 w-10 shrink-0 items-center justify-center rounded-full",
                "border border-glass-strong",
                ringing
                  ? "bg-violet-500/15 text-violet-300"
                  : "bg-emerald-500/15 text-emerald-300",
              )}
            >
              <Phone className={cn("h-5 w-5", ringing && "animate-pulse")} />
            </div>
            <div className="min-w-0">
              <p className="text-[10px] font-medium uppercase tracking-wider text-muted-foreground/60">
                {stateLabel}
              </p>
              {primary ? (
                <>
                  <p className="truncate text-base font-semibold text-foreground">
                    {displayName(primary)}
                  </p>
                  <p className="truncate text-xs text-muted-foreground">
                    {phone || "Unknown number"}
                  </p>
                  {primary.linkedCompanyName ? (
                    <p className="mt-0.5 flex min-w-0 items-center gap-1 text-xs text-muted-foreground">
                      <Building2 className="h-3 w-3 shrink-0 text-muted-foreground/70" />
                      <span className="truncate">{primary.linkedCompanyName}</span>
                      {primary.linkedCompanyRole && primary.linkedCompanyRole !== "primary" ? (
                        <span className="shrink-0 rounded-sm border border-glass bg-glass px-1 py-px text-[9px] uppercase tracking-wider text-muted-foreground/70">
                          {ROLE_LABEL[primary.linkedCompanyRole]}
                        </span>
                      ) : null}
                    </p>
                  ) : null}
                </>
              ) : (
                <>
                  <p className="truncate text-base font-semibold text-foreground">
                    {phone || "Unknown caller"}
                  </p>
                  <p className="truncate text-xs text-muted-foreground">
                    {lookup.isLoading
                      ? "Looking up contact…"
                      : phone.length === 0
                        ? "Anonymous"
                        : lookup.data?.phoneE164 === null
                          ? "Unrecognised number"
                          : "No matching contact"}
                  </p>
                </>
              )}
            </div>
          </div>
          <button
            type="button"
            onClick={() => dismiss(current.callId)}
            aria-label="Dismiss call popup"
            className="rounded-md p-1 text-muted-foreground/60 transition-colors hover:bg-glass-hover hover:text-foreground"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        {match.length > 1 ? (
          <div className="px-4 pb-2 text-[11px] text-muted-foreground/70">
            {match.length} contacts share this number — opening the first.
          </div>
        ) : null}

        <div className="flex items-center gap-2 px-4 pb-4 pt-1">
          {primary ? (
            <>
              {/* All three actions share one outlined-glass style: subtle
                  white border, faint fill, hover bumps both. flex-1 gives
                  each button an equal slice of the 360px card so the row
                  fits a single line without wrapping. */}
              <NewTicketDrawer
                initialContactId={primary.id}
                onOpenChange={(drawerOpen) => {
                  if (drawerOpen) {
                    setCreatingTicket(true);
                  } else if (creatingTicket) {
                    // Drawer is gone — safe to drop the popup. Whether
                    // the agent submitted or cancelled doesn't matter
                    // here; either way the call interaction is done.
                    setCreatingTicket(false);
                    dismiss(current.callId);
                  }
                }}
              >
                <Button size="sm" className={CALL_ACTION_BTN}>
                  Create ticket
                </Button>
              </NewTicketDrawer>
              <Button
                size="sm"
                className={CALL_ACTION_BTN}
                onClick={() => {
                  // ContactDetailPage reads window.location.hash on mount to
                  // pick the initial tab, so the #tickets fragment lands
                  // directly on the Tickets tab instead of bouncing through
                  // Overview.
                  void navigate({
                    to: "/contacts/$contactId",
                    params: { contactId: primary.id },
                    hash: "tickets",
                  });
                  dismiss(current.callId);
                }}
              >
                View tickets
              </Button>
              <Button
                size="sm"
                className={CALL_ACTION_BTN}
                disabled={!primary.linkedCompanyId}
                title={
                  primary.linkedCompanyId
                    ? undefined
                    : "Contact is not linked to a company"
                }
                onClick={() => {
                  if (!primary.linkedCompanyId) return;
                  // CompanyDetailPage reads window.location.hash on mount
                  // to pick the initial tab — same pattern as the contact
                  // detail page, so #tickets jumps straight to the
                  // company's Tickets tab.
                  void navigate({
                    to: "/companies/$companyId",
                    params: { companyId: primary.linkedCompanyId },
                    hash: "tickets",
                  });
                  dismiss(current.callId);
                }}
              >
                Company tickets
              </Button>
            </>
          ) : phone.length > 0 && lookup.data?.phoneE164 ? (
            <>
              {/* Two outlined-glass buttons matching the matched-contact
                  branch — same look, same heights, same flex-1 width
                  treatment. Order is deliberate: "link first" because the
                  caller is more often an existing contact whose number we
                  just don't know yet. */}
              <Button
                size="sm"
                className={CALL_ACTION_BTN}
                onClick={() => setLinkPickerOpen(true)}
              >
                Link to existing contact
              </Button>
              <Button
                size="sm"
                className={CALL_ACTION_BTN}
                onClick={() => setCreateContactOpen(true)}
              >
                Create contact
              </Button>
            </>
          ) : (
            <Button
              size="sm"
              variant="ghost"
              className="h-8"
              onClick={() => dismiss(current.callId)}
            >
              <PhoneOff className="mr-1.5 h-3.5 w-3.5" />
              Dismiss
            </Button>
          )}
        </div>
      </motion.div>
    </AnimatePresence>

    <ContactFormDialog
      open={createContactOpen}
      mode="create"
      initialPhone={phoneForLinking}
      onClose={() => setCreateContactOpen(false)}
      onSaved={() => {
        setCreateContactOpen(false);
        dismiss(current.callId);
      }}
    />

    <LinkCallerToContactDialog
      open={linkPickerOpen}
      phoneE164={phoneForLinking}
      onClose={() => setLinkPickerOpen(false)}
      onLinked={() => {
        setLinkPickerOpen(false);
        dismiss(current.callId);
      }}
    />
    </>
  );
}

/// Small two-step dialog that lets the agent link an unknown caller's
/// number to an existing contact. Step 1 is a contact search; step 2 is
/// the slot picker (phone vs mobile) with current values shown next to
/// each option, plus an inline overwrite-confirm when the chosen slot is
/// already filled. We re-fetch the contact between steps to make sure
/// the slot-state shown is authoritative — the list endpoint's row could
/// be slightly stale.
function LinkCallerToContactDialog({
  open,
  phoneE164,
  onClose,
  onLinked,
}: {
  open: boolean;
  phoneE164: string;
  onClose: () => void;
  onLinked: () => void;
}) {
  const qc = useQueryClient();
  const [search, setSearch] = React.useState("");
  const [debounced, setDebounced] = React.useState("");
  const [picked, setPicked] = React.useState<Contact | null>(null);
  const [confirmSlot, setConfirmSlot] = React.useState<
    "phone" | "mobilePhone" | null
  >(null);
  const [linking, setLinking] = React.useState(false);

  React.useEffect(() => {
    if (!open) return;
    setSearch("");
    setDebounced("");
    setPicked(null);
    setConfirmSlot(null);
    setLinking(false);
  }, [open]);

  React.useEffect(() => {
    const id = setTimeout(() => setDebounced(search), 250);
    return () => clearTimeout(id);
  }, [search]);

  const { data: contacts, isFetching } = useQuery({
    queryKey: ["contacts", "call-link-picker", debounced],
    queryFn: () => contactApi.list(debounced || undefined),
    enabled: open && picked === null,
    placeholderData: (prev) => prev,
  });

  async function handleSelectContact(c: Contact) {
    if (linking) return;
    setLinking(true);
    try {
      const full = await contactApi.get(c.id);
      setPicked(full);
    } catch {
      toast.error("Could not load contact");
    } finally {
      setLinking(false);
    }
  }

  async function persistSlot(slot: "phone" | "mobilePhone") {
    if (!picked || linking) return;
    setLinking(true);
    try {
      await contactApi.update(picked.id, {
        email: picked.email,
        firstName: picked.firstName,
        lastName: picked.lastName,
        phone: slot === "phone" ? phoneE164 : picked.phone,
        mobilePhone: slot === "mobilePhone" ? phoneE164 : picked.mobilePhone,
        jobTitle: picked.jobTitle,
      });
      qc.invalidateQueries({ queryKey: ["contacts"] });
      qc.invalidateQueries({ queryKey: ["telavox", "lookup"] });
      qc.invalidateQueries({ queryKey: ["contact", picked.id] });
      toast.success(
        slot === "phone" ? "Phone updated" : "Mobile phone updated",
      );
      onLinked();
    } catch (e) {
      if (e instanceof ApiError) {
        toast.error("Could not update contact");
      } else {
        toast.error("Could not update contact");
      }
      setLinking(false);
    }
  }

  function handleSlotClick(slot: "phone" | "mobilePhone") {
    if (!picked) return;
    const existing = slot === "phone" ? picked.phone : picked.mobilePhone;
    if (existing) {
      setConfirmSlot(slot);
    } else {
      void persistSlot(slot);
    }
  }

  const pickedName = picked
    ? [picked.firstName, picked.lastName].filter(Boolean).join(" ") ||
      picked.email ||
      "Contact"
    : "";

  return (
    <Dialog open={open} onOpenChange={(v) => (!v ? onClose() : null)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            {picked ? (
              <button
                type="button"
                onClick={() => {
                  if (confirmSlot) {
                    setConfirmSlot(null);
                  } else {
                    setPicked(null);
                  }
                }}
                className="rounded-md p-1 text-muted-foreground hover:bg-glass-hover hover:text-foreground"
                aria-label="Back"
              >
                <ArrowLeft className="h-4 w-4" />
              </button>
            ) : null}
            Link to existing contact
          </DialogTitle>
          <DialogDescription>
            {picked
              ? confirmSlot
                ? `Overwrite the existing ${
                    confirmSlot === "phone" ? "phone" : "mobile phone"
                  } on ${pickedName}?`
                : `Where do you want ${phoneE164} stored on ${pickedName}?`
              : `Pick the contact you want to link ${phoneE164} to.`}
          </DialogDescription>
        </DialogHeader>

        {!picked ? (
          <div className="space-y-3">
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search contacts…"
              autoFocus
              className="border-glass bg-glass"
            />
            <div className="max-h-[280px] overflow-y-auto rounded border border-glass">
              {!contacts ? (
                <p className="p-4 text-sm text-muted-foreground">Loading…</p>
              ) : contacts.length === 0 ? (
                <p className="p-4 text-sm text-muted-foreground">
                  No contacts found
                </p>
              ) : (
                contacts.map((c) => {
                  const name = [c.firstName, c.lastName]
                    .filter(Boolean)
                    .join(" ");
                  const phoneBits = [c.phone, c.mobilePhone].filter(Boolean);
                  const phoneInfo = phoneBits.length
                    ? phoneBits.join(" · ")
                    : "No phone yet";
                  return (
                    <button
                      key={c.id}
                      type="button"
                      disabled={linking}
                      onClick={() => handleSelectContact(c)}
                      className={cn(
                        "block w-full px-3 py-2 text-left text-sm transition-colors",
                        "hover:bg-glass-hover disabled:opacity-50",
                      )}
                    >
                      <div className="font-medium text-foreground">
                        {name || c.email || "Unnamed"}
                      </div>
                      <div className="text-xs text-muted-foreground">
                        {c.email}
                      </div>
                      <div className="text-xs text-muted-foreground/70">
                        {phoneInfo}
                      </div>
                    </button>
                  );
                })
              )}
              {isFetching && contacts && (
                <p className="px-3 py-1 text-[10px] text-muted-foreground/60">
                  Refreshing…
                </p>
              )}
            </div>
          </div>
        ) : confirmSlot ? (
          <div className="space-y-3">
            <div className="rounded border border-amber-500/30 bg-amber-500/10 p-3 text-sm">
              <p className="text-amber-200">
                {confirmSlot === "phone" ? "Phone" : "Mobile phone"} already
                contains{" "}
                <span className="font-mono">
                  {confirmSlot === "phone" ? picked.phone : picked.mobilePhone}
                </span>
                .
              </p>
              <p className="mt-1 text-xs text-amber-200/80">
                Continuing replaces it with{" "}
                <span className="font-mono">{phoneE164}</span>. This can't be
                undone from here.
              </p>
            </div>
            <div className="flex justify-end gap-2">
              <Button
                type="button"
                variant="ghost"
                onClick={() => setConfirmSlot(null)}
                disabled={linking}
              >
                Cancel
              </Button>
              <Button
                type="button"
                className="bg-amber-500/80 text-zinc-900 hover:bg-amber-400"
                onClick={() => void persistSlot(confirmSlot)}
                disabled={linking}
              >
                Overwrite
              </Button>
            </div>
          </div>
        ) : (
          <div className="space-y-2">
            <SlotTile
              icon={<Phone className="h-4 w-4" />}
              label="Phone"
              current={picked.phone}
              disabled={linking}
              onClick={() => handleSlotClick("phone")}
            />
            <SlotTile
              icon={<Smartphone className="h-4 w-4" />}
              label="Mobile phone"
              current={picked.mobilePhone}
              disabled={linking}
              onClick={() => handleSlotClick("mobilePhone")}
            />
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

function SlotTile({
  icon,
  label,
  current,
  disabled,
  onClick,
}: {
  icon: React.ReactNode;
  label: string;
  current: string;
  disabled: boolean;
  onClick: () => void;
}) {
  const filled = current.length > 0;
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={cn(
        "flex w-full items-center justify-between gap-3 rounded-lg border px-3 py-3 text-left transition-colors",
        "border-glass bg-glass hover:bg-glass-hover disabled:opacity-50",
      )}
    >
      <span className="flex items-center gap-3 min-w-0">
        <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-glass bg-glass text-muted-foreground">
          {icon}
        </span>
        <span className="min-w-0">
          <span className="block text-sm font-medium text-foreground">
            {label}
          </span>
          <span className="block truncate text-xs text-muted-foreground">
            {filled ? (
              <>
                Currently:{" "}
                <span className="font-mono text-amber-300/90">{current}</span>
              </>
            ) : (
              "Empty"
            )}
          </span>
        </span>
      </span>
      <span
        className={cn(
          "shrink-0 rounded-md border px-2 py-0.5 text-[10px] uppercase tracking-wider",
          filled
            ? "border-amber-400/40 bg-amber-500/10 text-amber-200"
            : "border-emerald-400/30 bg-emerald-500/10 text-emerald-200",
        )}
      >
        {filled ? "Replace" : "Add"}
      </span>
    </button>
  );
}
