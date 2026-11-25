import { describe, expect, it } from "vitest";
import type { CampaignRecipient, CampaignRecipientState } from "@/lib/campaigns-api";
import { filterRecipients } from "./campaignRecipientFilters";
import { passesNavFlagGate } from "@/shell/navFlagGates";
import type { AuthUser } from "@/auth/authStore";

function r(ticketId: string, state: CampaignRecipientState, decision: CampaignRecipient["decision"] = null): CampaignRecipient {
  return { ticketId, state, decision } as CampaignRecipient;
}

describe("campaign recipient filters", () => {
  const rows = [
    r("reply", "ReplyReceived"),
    r("timer", "TimerExpired"),
    r("decided", "ReplyReceived", "Approved"),
    r("waiting", "Waiting"),
    r("queued", "Queued"),
    r("new", "NotSent"),
    r("failed", "Failed"),
    r("skipped", "Skipped"),
  ];
  const ids = (f: Parameters<typeof filterRecipients>[1]) => filterRecipients(rows, f).map((x) => x.ticketId);

  it("needs attention = an answer or an expired timer without a decision", () => {
    expect(ids("attention")).toEqual(["reply", "timer"]);
  });
  it("a decision moves a ticket out of the work lists", () => {
    expect(ids("decided")).toEqual(["decided"]);
    expect(ids("waiting")).toEqual(["waiting", "queued"]);
  });
  it("problems and not-sent are their own lists, all shows everything", () => {
    expect(ids("problems")).toEqual(["failed", "skipped"]);
    expect(ids("notSent")).toEqual(["new"]);
    expect(ids("all")).toHaveLength(rows.length);
  });
});

describe("nav flag gates", () => {
  it("hide Campaigns unless the user has the flag", () => {
    expect(passesNavFlagGate("/campaigns", { campaignsEnabled: false } as AuthUser)).toBe(false);
    expect(passesNavFlagGate("/campaigns", { campaignsEnabled: true } as AuthUser)).toBe(true);
    expect(passesNavFlagGate("/campaigns", null)).toBe(false);
  });
  it("leave ungated entries alone and keep the SLA log on unless SLA is off", () => {
    expect(passesNavFlagGate("/tickets", null)).toBe(true);
    expect(passesNavFlagGate("/sla-log", { slaEnabled: true } as AuthUser)).toBe(true);
    expect(passesNavFlagGate("/sla-log", { slaEnabled: false } as AuthUser)).toBe(false);
  });
});
