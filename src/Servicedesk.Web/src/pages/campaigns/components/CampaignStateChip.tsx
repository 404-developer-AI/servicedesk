import { cn } from "@/lib/utils";
import {
  CAMPAIGN_DECISION_LABELS,
  CAMPAIGN_STATE_LABELS,
  type CampaignDecision,
  type CampaignRecipientState,
} from "@/lib/campaigns-api";

/// v0.1.67 — the fixed chip vocabulary of a campaign recipient, theme tokens
/// only (same palette as the back-office chips):
/// - Reply received / Timer expired: amber — needs an agent
/// - Waiting / Queued: primary — in progress
/// - Failed: destructive
/// - Not sent / Skipped: neutral
const STYLE: Record<CampaignRecipientState, string> = {
  ReplyReceived: "border-amber-400/45 bg-amber-100/70 text-amber-900 dark:bg-amber-400/12 dark:text-amber-200",
  TimerExpired: "border-amber-400/45 bg-amber-100/70 text-amber-900 dark:bg-amber-400/12 dark:text-amber-200",
  Waiting: "border-primary/30 bg-primary/10 text-primary",
  Queued: "border-primary/30 bg-primary/10 text-primary",
  Failed: "border-destructive/40 bg-destructive/10 text-destructive",
  NotSent: "border-glass-strong bg-glass text-muted-foreground",
  Skipped: "border-glass-strong bg-glass text-muted-foreground",
};

const CHIP = "sd-campaign-chip inline-flex max-w-full items-center truncate rounded-md border px-2 py-0.5 text-[11px] font-medium";

export function CampaignStateChip({ state, className }: { state: CampaignRecipientState; className?: string }) {
  return <span className={cn(CHIP, STYLE[state], className)}>{CAMPAIGN_STATE_LABELS[state]}</span>;
}

export function CampaignDecisionChip({ decision, className }: { decision: CampaignDecision; className?: string }) {
  const style = decision === "Approved"
    ? "border-primary/30 bg-primary/10 text-primary"
    : decision === "Declined"
      ? "border-destructive/40 bg-destructive/10 text-destructive"
      : "border-glass-strong bg-glass text-foreground";
  return <span className={cn(CHIP, style, className)}>{CAMPAIGN_DECISION_LABELS[decision]}</span>;
}
