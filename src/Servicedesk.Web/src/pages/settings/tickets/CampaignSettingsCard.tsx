import { Megaphone } from "lucide-react";
import { CollapsibleSettingsCard } from "@/components/settings/CollapsibleSettingsCard";

const CAMPAIGN_SETTINGS: ReadonlyArray<{ key: string; label: string }> = [
  { key: "Campaigns.Enabled", label: "Allow sending campaigns" },
  { key: "Campaigns.MaxRecipients", label: "Maximum tickets per campaign" },
  { key: "Campaigns.SendRatePerMinute", label: "Mails per minute" },
  { key: "Campaigns.MaxAttempts", label: "Attempts after a temporary error" },
  { key: "Campaigns.ProposedDateFormat", label: "Proposed date format" },
  { key: "Campaigns.ProposedTimeFormat", label: "Proposed time format" },
  { key: "Campaigns.ProposedDateCulture", label: "Language of day and month names" },
  { key: "Campaigns.ApprovalLinkLabel", label: "Answer link text" },
  { key: "Campaigns.ApprovalLinkMaxDays", label: "Answer link valid at most (days)" },
  { key: "Campaigns.MaxScheduleDays", label: "Schedule at most (days ahead)" },
  { key: "Campaigns.MaintenanceEnabled", label: "Run the maintenance planner" },
];

/// v0.1.67 — Campaigns (bulk mail) knobs under Settings → Tickets → General.
/// Who may use Campaigns is the per-user flag (Settings → Users).
export function CampaignSettingsCard() {
  return (
    <CollapsibleSettingsCard
      category="Tickets"
      icon={<Megaphone className="h-5 w-5" />}
      title="Campaigns"
      description="Mail many tickets at once — one personalised mail per ticket, sent from that ticket through the paced campaign outbox. Who may use it is the per-user Campaigns flag under Settings → Users."
      keys={CAMPAIGN_SETTINGS}
    />
  );
}
