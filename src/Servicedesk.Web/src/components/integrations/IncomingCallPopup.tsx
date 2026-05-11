import * as React from "react";
import { AnimatePresence, motion } from "framer-motion";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Phone, PhoneOff, UserPlus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useIncomingCallStore } from "@/stores/useIncomingCallStore";
import { contactApi, type Contact } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

/// Treat any value in this set as "no active call" — the worker pushes
/// these as the call terminates. We dismiss the popup automatically so
/// the agent's screen doesn't stay covered by a ghost card. Matches the
/// upstream Telavox vocabulary plus the synonyms the state-machine
/// normalises ("ENDED" is the canonical terminal in the server tests).
const TERMINAL_STATES = new Set(["ENDED", "HANGUP", "TERMINATED", "DROPPED"]);

function isRinging(state: string): boolean {
  const s = state.toUpperCase();
  return s === "RINGING" || s === "ALERTING";
}

function isAnswered(state: string): boolean {
  const s = state.toUpperCase();
  return s === "ANSWERED" || s === "CONNECTED" || s === "ACTIVE";
}

function displayName(c: Contact): string {
  const full = `${c.firstName ?? ""} ${c.lastName ?? ""}`.trim();
  return full.length > 0 ? full : (c.email || "Contact");
}

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

  // Auto-dismiss on terminal states. The worker debounces same-state
  // ticks server-side, so an ENDED push is the cleanup signal.
  React.useEffect(() => {
    if (!current) return;
    if (TERMINAL_STATES.has(current.state.toUpperCase())) {
      // Slight delay so the agent has a chance to see "call ended" if
      // they were still looking at the popup. Not configurable yet —
      // a hard 4s is short enough to feel responsive and long enough
      // for a glance.
      const id = window.setTimeout(() => dismiss(current.callId), 4000);
      return () => window.clearTimeout(id);
    }
    return;
  }, [current, dismiss]);

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

  return (
    // mode="wait" so a fast-replaced call (call A ENDED, call B RINGING
    // arrives within the exit animation) doesn't stack two motion.divs
    // for one frame in the bottom-right corner.
    <AnimatePresence mode="wait">
      <motion.div
        key={current.callId}
        initial={{ opacity: 0, x: 16, y: 16 }}
        animate={{ opacity: 1, x: 0, y: 0 }}
        exit={{ opacity: 0, x: 16, y: 16 }}
        transition={{ type: "spring", stiffness: 280, damping: 26 }}
        className={cn(
          "pointer-events-auto fixed bottom-6 right-6 z-[60] w-[360px] overflow-hidden",
          "rounded-2xl border border-white/[0.08] shadow-2xl",
          "bg-gradient-to-br from-zinc-900/90 to-zinc-950/90 backdrop-blur-xl",
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
                "border border-white/[0.08]",
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
            className="rounded-md p-1 text-muted-foreground/60 transition-colors hover:bg-white/[0.05] hover:text-foreground"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        {match.length > 1 ? (
          <div className="px-4 pb-2 text-[11px] text-muted-foreground/70">
            {match.length} contacts share this number — opening the first.
          </div>
        ) : null}

        <div className="flex flex-wrap gap-2 px-4 pb-4 pt-1">
          {primary ? (
            <>
              <Button
                size="sm"
                className="h-8 bg-gradient-to-r from-violet-600 to-indigo-600 text-white hover:opacity-90"
                onClick={() => {
                  void navigate({
                    to: "/contacts/$contactId",
                    params: { contactId: primary.id },
                  });
                  dismiss(current.callId);
                }}
              >
                Open contact
              </Button>
              <Button
                size="sm"
                variant="ghost"
                className="h-8"
                onClick={() => {
                  // The contact-detail page has the tickets-for-this-contact
                  // list inline, so a single navigate covers both "open
                  // existing" and "see context". A future iteration can
                  // surface a "+ New ticket" trigger here once the global
                  // NewTicketDrawer accepts a contactId pre-fill.
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
            </>
          ) : phone.length > 0 && lookup.data?.phoneE164 ? (
            <>
              <Button
                size="sm"
                className="h-8 bg-gradient-to-r from-violet-600 to-indigo-600 text-white hover:opacity-90"
                onClick={() => {
                  // Routes the agent to global search pre-filtered by the
                  // E.164 number so adjacent rows (tickets where this
                  // number was mentioned, mail bodies, etc.) surface
                  // alongside the contact-search hit. From there the
                  // Contacts page handles the actual "create contact"
                  // dialog. Keeps the popup tight without losing the
                  // primary "I don't know this number" flow.
                  void navigate({
                    to: "/search",
                    search: {
                      q: lookup.data?.phoneE164 ?? phone,
                      type: undefined,
                      offset: undefined,
                    },
                  });
                  dismiss(current.callId);
                }}
              >
                <UserPlus className="mr-1.5 h-3.5 w-3.5" />
                Search number
              </Button>
              <Button
                size="sm"
                variant="ghost"
                className="h-8"
                onClick={() => dismiss(current.callId)}
              >
                Dismiss
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
  );
}
