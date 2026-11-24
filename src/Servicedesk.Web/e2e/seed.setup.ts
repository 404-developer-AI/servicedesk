import { randomBytes } from "node:crypto";
import { expect, test as setup } from "@playwright/test";
import { api } from "./support/api";
import { freshTotp } from "./support/totp";
import { writeState } from "./support/state";

/**
 * Seeds a fresh install the way an operator would: the first-run setup
 * creates the admin, who enrols TOTP (mandatory for staff), adds a queue
 * and two priorities, and one company with a requester contact.
 * Fictitious data only. Refuses to run against an install that already has
 * users — the suite never touches real data.
 */
setup("seed a fresh install", async ({ request }) => {
  const status = await (await request.get("/api/auth/setup/status")).json();
  expect(
    status.available,
    "The e2e suite needs a fresh install (no users yet). Point E2E_BASE_URL at an empty e2e database.",
  ).toBe(true);

  const email = "e2e.admin@example.test";
  const password = randomBytes(18).toString("base64url");
  await api(request, "POST", "/api/auth/setup/create-admin", { email, password });

  const { secret } = await api<{ secret: string }>(request, "POST", "/api/auth/2fa/enroll/begin");
  const { code, step } = await freshTotp(secret, null);
  const { recoveryCodes } = await api<{ recoveryCodes: string[] }>(request, "POST", "/api/auth/2fa/enroll/confirm", { code });
  expect(recoveryCodes.length).toBeGreaterThan(0);

  // A fresh install ships statuses only: the operator adds a queue and
  // priorities before the first ticket.
  type Created = { id: string };
  const queue = await api<Created>(request, "POST", "/api/taxonomy/queues", { name: "Support", sortOrder: 0, isActive: true });
  const normal = await api<Created>(request, "POST", "/api/taxonomy/priorities", { name: "Normal", level: 2, sortOrder: 0, isActive: true, isDefault: true });
  const high = await api<Created>(request, "POST", "/api/taxonomy/priorities", { name: "High", level: 3, sortOrder: 1, isActive: true, isDefault: false });
  const statuses = await api<{ id: string; isDefault: boolean }[]>(request, "GET", "/api/taxonomy/statuses");
  const newStatus = statuses.find((s) => s.isDefault)!;

  const requesterEmail = "pat.requester@example.test";
  const company = await api<Created>(request, "POST", "/api/companies", { name: "Example Corp", code: "E2E001" });
  const contact = await api<Created>(request, "POST", "/api/contacts", {
    firstName: "Pat", lastName: "Requester", email: requesterEmail, companyId: company.id, role: "primary",
  });

  writeState({
    email, password, totpSecret: secret, lastUsedStep: step, requesterEmail,
    ids: { contact: contact.id, queue: queue.id, newStatus: newStatus.id, normalPriority: normal.id, highPriority: high.id },
  });
});
