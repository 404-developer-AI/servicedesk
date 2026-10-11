/// Shared by the desktop timeline and the mobile ticket page; kept out of
/// TicketTimeline.tsx so that module only exports components (fast refresh).

/// Left-edge accent colour per content-event type, tying each card back to
/// its timeline dot — sky for mail (in/out), blue for internal notes,
/// emerald for customer-visible replies. Internal events override this
/// with amber (see the card className in TicketTimeline).
export const CARD_ACCENT: Record<string, string> = {
  MailReceived: "border-l-sky-400/60",
  MailSent: "border-l-sky-500/50",
  Mail: "border-l-sky-500/50",
  Note: "border-l-blue-500/50",
  Comment: "border-l-emerald-500/40",
  PortalMessage: "border-l-teal-500/50",
  Call: "border-l-violet-500/60",
};

export function parseMetadata(json: string): Record<string, unknown> {
  try {
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return {};
  }
}
