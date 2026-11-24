import { createHash, randomBytes } from "node:crypto";
import { expect, test } from "@playwright/test";
import { api } from "./support/api";
import { pgUrl, sql } from "./support/db";
import { readState } from "./support/state";
import { freshTotp } from "./support/totp";
import { seedTicket } from "./support/ui";

/**
 * Flow 6 — the customer portal, end to end: an agent invites the requester,
 * the customer accepts the invitation, signs in with a password and the
 * mandatory TOTP enrolment, and sees their ticket — the agent's public
 * reply, never the internal note (portal whitelist).
 *
 * The invitation mail is the one step a browser cannot do: the test stands
 * in for the inbox by minting the invitation token itself, stored hashed
 * exactly like PortalAccountService.InviteAsync does (SHA-256 of 32 random
 * bytes, base64url in the link). Everything after that is the real flow.
 */
test.describe.configure({ mode: "serial" });

test.skip(!pgUrl() && !process.env.CI, "Set E2E_PG_URL (the e2e database) to run the portal flow.");

const internalNote = `Internal: check the contract before replying ${Date.now().toString(36)}`;
const publicReply = `We replaced the toner cartridge ${Date.now().toString(36)}`;
const subject = `E2E portal printer ${Date.now().toString(36)}`;
const customerPassword = randomBytes(18).toString("base64url");
let inviteToken = "";

test("an agent prepares the ticket and the invitation", async ({ request }) => {
  const { ids, requesterEmail } = readState();
  await api(request, "PUT", "/api/settings/Portal.Enabled", { value: "true" });

  const ticket = await seedTicket(request, subject);
  await api(request, "POST", `/api/tickets/${ticket.id}/events`, {
    eventType: "Comment", bodyHtml: `<p>${publicReply}</p>`, isInternal: false,
  });
  await api(request, "POST", `/api/tickets/${ticket.id}/events`, {
    eventType: "Note", bodyHtml: `<p>${internalNote}</p>`, isInternal: true,
  });

  // The invitation mail's token, minted the way the server mints it.
  const raw = randomBytes(32);
  inviteToken = raw.toString("base64url");
  sql(
    `INSERT INTO portal_tokens (kind, token_hash, email, contact_id, company_id, company_role,
                                display_name, expires_utc, company_links)
     VALUES ('Invitation', decode(:'hash', 'hex'), :'email', :'contact'::uuid, :'company'::uuid, 'Member',
             'Pat Requester', now() + interval '1 day',
             jsonb_build_array(jsonb_build_object('companyId', :'company', 'role', 'Member')))`,
    {
      hash: createHash("sha256").update(raw).digest("hex"),
      email: requesterEmail,
      contact: ids.contact,
      company: ids.company,
    },
  );
});

test("the customer accepts, enrols TOTP and sees only the public conversation", async ({ browser }) => {
  const { requesterEmail } = readState();
  // A fresh browser: no staff session.
  const context = await browser.newContext({ storageState: { cookies: [], origins: [] } });
  const page = await context.newPage();

  await page.goto(`/portal/invitation?token=${encodeURIComponent(inviteToken)}`);
  const passwords = page.locator('input[autocomplete="new-password"]');
  await passwords.nth(0).fill(customerPassword);
  await passwords.nth(1).fill(customerPassword);
  await page.locator('button[type="submit"]').click();

  await page.waitForURL(/\/portal\/login/);
  await page.locator('input[autocomplete="username"]').fill(requesterEmail);
  await page.locator('input[autocomplete="current-password"]').fill(customerPassword);
  const enrolment = page.waitForResponse((r) => r.url().includes("/api/portal/auth/2fa/enroll/begin") && r.ok());
  await page.locator('button[type="submit"]').click();

  // Mandatory TOTP for customers: the enrolment step shows the secret.
  const { secret } = (await (await enrolment).json()) as { secret: string };
  const { code } = await freshTotp(secret, null);
  await page.locator('input[autocomplete="one-time-code"]').fill(code);
  await page.locator('button[type="submit"]').click();
  await page.getByRole("button", { name: /continue|saved|done/i }).click();

  await page.waitForURL(/\/portal(\/|$)/);
  await page.getByText(subject).click();
  await expect(page.getByText(publicReply)).toBeVisible();
  await expect(page.getByText(internalNote)).toHaveCount(0);
  expect(await page.content()).not.toContain(internalNote);

  await context.close();
});
