import type { Queue } from "@/lib/api";
import type { MailContext } from "./components/SendMailForm";

/// Build the MailContext record that feeds SendMailForm's Reply / Reply-all /
/// Forward / New pre-fill logic. Pulls the most-recent MailReceived event's
/// metadata as the "latest inbound", wires the requester's email for
/// compose-new, and accepts the caller's flattened queue-mailbox list so
/// reply-all can strip them (plus any `+suffix` variants) back out.
export function buildMailContext(
  ticket: any,
  events: any[],
  requesterEmail: string | null,
  ownMailboxAddresses: string[],
): MailContext {
  const latestMailReceived = [...events]
    .reverse()
    .find((e) => e?.eventType === "MailReceived");
  let latestInbound: MailContext["latestInbound"] = null;
  if (latestMailReceived) {
    try {
      const meta = latestMailReceived.metadataJson
        ? JSON.parse(latestMailReceived.metadataJson)
        : {};
      const fromAddr =
        typeof meta.from === "string" && meta.from.length > 0 ? meta.from : null;
      const fromName =
        typeof meta.fromName === "string" && meta.fromName.length > 0
          ? meta.fromName
          : fromAddr ?? "";
      const subject =
        typeof meta.subject === "string" ? meta.subject : null;
      const toArr = Array.isArray(meta.to) ? meta.to : [];
      const ccArr = Array.isArray(meta.cc) ? meta.cc : [];
      latestInbound = {
        from: fromAddr ? { address: fromAddr, name: fromName } : null,
        to: toArr
          .filter((r: any) => typeof r?.address === "string" && r.address.length > 0)
          .map((r: any) => ({ address: r.address, name: r.name ?? r.address })),
        cc: ccArr
          .filter((r: any) => typeof r?.address === "string" && r.address.length > 0)
          .map((r: any) => ({ address: r.address, name: r.name ?? r.address })),
        subject,
        bodyHtml: latestMailReceived.bodyHtml ?? null,
        receivedUtc: latestMailReceived.createdUtc ?? null,
      };
    } catch {
      // treat unparseable metadata as empty
    }
  }
  return {
    ticketSubject: ticket?.subject ?? "",
    ticketNumber: typeof ticket?.number === "number" ? ticket.number : 0,
    latestInbound,
    requesterEmail: requesterEmail && requesterEmail.length > 0 ? requesterEmail : null,
    ownMailboxAddresses,
  };
}

/// Flatten the accessible-queues list into a de-duplicated list of mailbox
/// addresses (inbound + outbound), used by SendMailForm to strip queue
/// mailboxes out of reply-all recipient fields.
export function flattenQueueMailboxes(queues: Queue[] | undefined): string[] {
  const out: string[] = [];
  for (const q of queues ?? []) {
    if (q.inboundMailboxAddress) out.push(q.inboundMailboxAddress);
    if (q.outboundMailboxAddress) out.push(q.outboundMailboxAddress);
  }
  return out;
}
