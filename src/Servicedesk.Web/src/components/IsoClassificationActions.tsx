import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, ShieldAlert, ShieldOff } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ticketApi, type Ticket } from "@/lib/ticket-api";
import { settingsApi, taxonomyApi, type Status } from "@/lib/api";
import { useAuth } from "@/auth/authStore";
import { cn } from "@/lib/utils";

// Slug of the ISO MGM-review status seeded in v0.0.40. Matches the
// constant on the server-side IsoEndpoints.IsoSlugs. Looking up by slug
// (not id) means admin label renames keep the buttons working.
const SLUG_MGM_REVIEW = "iso-mgm-review";

type Stage = "mgm-review" | "terminal" | "off-flow";

type Action =
  | { kind: "classify-event" }
  | { kind: "classify-incident" };

type Props = {
  ticket: Ticket;
};

/// v0.0.40 — MGM classification buttons on the ticket detail header.
/// Visible only when (a) the ticket sits in the queue bound to
/// Iso27001.QueueId, (b) the agent has the MGM ISO flag, and (c) the
/// ticket's current status is "MGM review". Each click opens a modal
/// that demands a 5+ char motivation; submit calls the matching
/// classification endpoint which atomically writes the motivation as an
/// internal note, flips the status, and audits the action.
export function IsoClassificationActions({ ticket }: Props) {
  const auth = useAuth();
  const user = auth.user;
  const qc = useQueryClient();

  // Read the bound queue setting + the ISO statuses in parallel. Long
  // staleTime — both change rarely and the same admin who edits either
  // also reloads the page.
  const isoSettingsQ = useQuery({
    queryKey: ["settings", "iso27001"],
    queryFn: () => settingsApi.list("Iso27001"),
    staleTime: 5 * 60_000,
    enabled: !!user && user.isIsoMgm,
  });
  const statusesQ = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: () => taxonomyApi.statuses.list(),
    staleTime: 5 * 60_000,
    enabled: !!user && user.isIsoMgm,
  });

  const [pendingAction, setPendingAction] = React.useState<Action | null>(null);

  // Hide entirely when the user lacks the MGM flag — the cheapest
  // possible no-op. We only fetch the settings / statuses when set.
  if (!user || !user.isIsoMgm) return null;

  const boundQueueId = isoSettingsQ.data?.find((e) => e.key === "Iso27001.QueueId")?.value ?? "";
  if (!boundQueueId || boundQueueId.trim().length === 0) return null;
  if (ticket.queueId !== boundQueueId) return null;

  if (isoSettingsQ.isLoading || statusesQ.isLoading) {
    return (
      <div className="flex items-center gap-2">
        <Skeleton className="h-8 w-32" />
      </div>
    );
  }

  const stage = resolveStage(ticket, statusesQ.data ?? []);
  if (stage === "off-flow" || stage === "terminal") return null;

  return (
    <>
      <div className="flex flex-wrap items-center gap-2">
        {stage === "mgm-review" && (
          <>
            <ActionButton
              onClick={() => setPendingAction({ kind: "classify-event" })}
              tone="emerald"
              icon={<ShieldOff className="h-3.5 w-3.5" />}
              label="Classify as event"
            />
            <ActionButton
              onClick={() => setPendingAction({ kind: "classify-incident" })}
              tone="amber"
              icon={<ShieldAlert className="h-3.5 w-3.5" />}
              label="Classify as incident"
            />
          </>
        )}
      </div>

      {pendingAction && (
        <MotivationDialog
          ticketId={ticket.id}
          action={pendingAction}
          onClose={() => setPendingAction(null)}
          onSuccess={() => {
            setPendingAction(null);
            // Refetch the ticket so the new status + the internal note
            // appear without a manual reload.
            qc.invalidateQueries({ queryKey: ["ticket", ticket.id] });
            qc.invalidateQueries({ queryKey: ["tickets"] });
          }}
        />
      )}
    </>
  );
}

function resolveStage(ticket: Ticket, statuses: Status[]): Stage {
  const status = statuses.find((s) => s.id === ticket.statusId);
  if (!status) return "off-flow";
  if (status.slug === SLUG_MGM_REVIEW) return "mgm-review";
  if (status.slug?.startsWith("iso-")) return "terminal";
  return "off-flow";
}

function ActionButton({
  onClick,
  tone,
  icon,
  label,
}: {
  onClick: () => void;
  tone: "amber" | "emerald";
  icon: React.ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors",
        tone === "emerald"
          ? "border-emerald-400/30 bg-emerald-400/10 text-emerald-200 hover:bg-emerald-400/20"
          : "border-amber-400/30 bg-amber-400/10 text-amber-200 hover:bg-amber-400/20",
      )}
    >
      {icon}
      {label}
    </button>
  );
}

function MotivationDialog({
  ticketId,
  action,
  onClose,
  onSuccess,
}: {
  ticketId: string;
  action: Action;
  onClose: () => void;
  onSuccess: () => void;
}) {
  const [motivation, setMotivation] = React.useState("");
  const trimmed = motivation.trim();
  const valid = trimmed.length >= 5 && trimmed.length <= 4000;

  const cfg = DIALOG_CONFIG[action.kind];

  const mutation = useMutation({
    mutationFn: async () => {
      switch (action.kind) {
        case "classify-event":
          return ticketApi.isoClassifyEvent(ticketId, trimmed);
        case "classify-incident":
          return ticketApi.isoClassifyIncident(ticketId, trimmed);
      }
    },
    onSuccess: () => {
      toast.success(cfg.successToast);
      onSuccess();
    },
    onError: (err: unknown) => {
      const msg = err instanceof Error ? err.message : "Classification failed";
      toast.error(msg);
    },
  });

  return (
    <Dialog open onOpenChange={(o) => !o && !mutation.isPending && onClose()}>
      <DialogContent className="max-w-md border-white/[0.06] bg-background/85 backdrop-blur-xl">
        <DialogHeader>
          <DialogTitle className="font-display">{cfg.title}</DialogTitle>
          <DialogDescription>{cfg.description}</DialogDescription>
        </DialogHeader>
        <div className="space-y-2">
          <label className="text-xs font-medium text-muted-foreground">
            Motivation
          </label>
          <textarea
            autoFocus
            value={motivation}
            onChange={(e) => setMotivation(e.target.value)}
            placeholder={cfg.placeholder}
            rows={4}
            className="w-full rounded-md border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-ring"
          />
          <p className="text-[11px] text-muted-foreground/70">
            {trimmed.length < 5
              ? `${5 - trimmed.length} more character${trimmed.length === 4 ? "" : "s"} required.`
              : `${trimmed.length} / 4000 characters.`}
          </p>
        </div>
        <DialogFooter>
          <Button
            variant="ghost"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            Cancel
          </Button>
          <Button
            onClick={() => mutation.mutate()}
            disabled={!valid || mutation.isPending}
            className={cn(
              cfg.confirmTone === "emerald"
                ? "bg-emerald-600 hover:bg-emerald-500"
                : "bg-amber-600 hover:bg-amber-500",
              "text-white",
            )}
          >
            {mutation.isPending && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            {cfg.confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

const DIALOG_CONFIG = {
  "classify-event": {
    title: "Classify as event",
    description:
      "Recording this ticket as an event closes it. The motivation lands as an internal note and is captured in the audit log for ISO 27001 conformance.",
    placeholder: "Why is this not an incident? (e.g. false-positive, duplicate of #1234, …)",
    successToast: "Ticket classified as event",
    confirmLabel: "Classify as event",
    confirmTone: "emerald",
  },
  "classify-incident": {
    title: "Classify as incident",
    description:
      "Recording this ticket as an incident hands it over to the DPO for ISMS registration. The motivation lands as an internal note and is captured in the audit log.",
    placeholder: "Why is this an incident? (impact, scope, affected systems …)",
    successToast: "Ticket escalated to DPO",
    confirmLabel: "Classify as incident",
    confirmTone: "amber",
  },
} as const;
