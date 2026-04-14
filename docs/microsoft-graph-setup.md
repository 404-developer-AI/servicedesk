# Microsoft Graph — mail polling setup

This guide walks an admin through registering the app in Azure AD and
configuring it in Servicedesk so the mail-polling service can pull mail
from each queue's inbound mailbox.

You only do this **once per install**. After that, adding new queue
mailboxes is a Settings-page action (no Azure changes needed) as long as
the mailboxes live in the same tenant the app is registered against.

## What you need
- Azure tenant administrator (or someone who can grant admin consent).
- The Microsoft 365 mailbox addresses you intend to poll (one per queue).

## 1. Create the app registration
1. Open the [Azure Portal](https://portal.azure.com) and go to
   **Entra ID → App registrations → New registration**.
2. Name it something recognizable, e.g. **Servicedesk — Mail**.
3. **Supported account types:** *Accounts in this organizational directory only*.
4. Leave the redirect URI blank.
5. Click **Register**.
6. On the **Overview** page, copy:
   - **Application (client) ID**
   - **Directory (tenant) ID**

## 2. Grant API permissions
1. Go to **API permissions → Add a permission → Microsoft Graph → Application permissions**.
2. Add:
   - `Mail.ReadWrite` — required. Covers the polling loop (read), the
     post-ingest "mark as read", and the move into the processed folder.
     `Mail.Read` alone is **not enough** — PATCH and Move both need write.
   - `Mail.Send` — reserved for per-queue send-as replies in a later release.
     Grant it now so you don't need a second round of admin consent later.
3. Click **Grant admin consent for <your tenant>**. All permissions must
   show a green checkmark.

> If you previously granted only `Mail.Read`, symptoms are: delta polling
> works (mails are ingested into tickets) but the server log shows
> `Access is denied` for mark-as-read and folder-create, and the Health
> page flips the `mail-polling` subsystem to **Warning**.

> **Why application permissions (not delegated)?** The polling loop runs
> as the app itself — there is no signed-in user at poll time. Application
> permissions are the Graph flow that matches this.

## 3. Create a client secret
1. Go to **Certificates & secrets → Client secrets → New client secret**.
2. Pick a description and an expiry (Azure caps this at 24 months).
   Put the expiry date on your own calendar — mail will stop flowing when
   the secret expires.
3. Click **Add** and **immediately copy the Value** column. Azure will
   never show it again.

## 4. Configure Servicedesk
1. Sign in as an admin.
2. Go to **Settings → Mail → Microsoft Graph**.
3. Paste the **Tenant ID** and **Client ID** from step 1.6.
4. Paste the **Client secret** from step 3.3 and click **Save**. The
   secret is encrypted at rest (ASP.NET Core DataProtection) before it
   hits the database — plaintext never leaves the request.
5. In the **Test connection** row, enter one of your mailbox addresses
   and click **Test**. You should see `OK — <ms>`. If you see an error,
   double-check:
   - Admin consent was granted (not just "added").
   - The mailbox address is correct and belongs to the same tenant.
   - The app registration is not disabled.

## 5. Map mailboxes to queues
1. Go to **Settings → Tickets → Queues** and open the queue you want.
2. Fill in **Inbound mailbox address** (e.g. `support@company.com`).
3. Optionally set **Outbound mailbox address** — this is reserved for
   per-queue send-as replies in a later release; it has no runtime effect
   today.
4. Save. Within one polling interval (default: 60 seconds) the service
   starts pulling mail for that queue. Watch the server log for lines
   like:
   ```
   [MailPolling] queue=servicedesk mailbox=support@company.com received 3 message(s)
   ```

Each queue keeps its own Graph delta cursor, so a restart never
reprocesses old mail.

## Troubleshooting
- **"Authorization_IdentityNotFound"** — wrong tenant ID, or the mailbox
  isn't in this tenant.
- **"Authorization_RequestDenied"** — admin consent wasn't granted, or
  the mailbox is excluded by an ApplicationAccessPolicy.
- **"invalid_client"** — the client secret is wrong, expired, or has
  been deleted in Azure.
- **Errors visible on the test connection** — the exact message is
  surfaced directly from Graph; use it to pinpoint the issue.

## Security notes
- The client secret is stored in the `protected_secrets` table,
  encrypted with the app's DataProtection key ring. Rotate the secret in
  Azure and re-paste it here whenever it approaches expiry.
- Never commit the secret to git or paste it into an issue or chat.
- Consider narrowing the app's mailbox scope with an
  [ApplicationAccessPolicy](https://learn.microsoft.com/en-us/graph/auth-limit-mailbox-access)
  so it can only read the exact mailboxes you configure — a defensive
  guardrail against misconfigured permissions.
