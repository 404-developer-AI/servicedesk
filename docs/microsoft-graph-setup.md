# Microsoft 365 — mail + login setup

This guide walks an admin through registering the app in Azure AD / Entra ID
and configuring it in Servicedesk. One app registration covers **both**:

- Mail polling (inbound) and outbound mail (`Mail.ReadWrite`, `Mail.Send` — application).
- Microsoft 365 sign-in for agents and admins (`openid`, `profile`, `email`, `User.Read`
  delegated + `User.Read.All` application).

You only do this **once per install**. Adding queue mailboxes afterwards is a
Settings-page action with no Azure changes. Adding / removing agents is also
done in Servicedesk (Settings → Users) — those changes never round-trip back to
Azure.

## What you need
- Azure tenant administrator (or someone who can grant admin consent).
- The Microsoft 365 mailbox addresses you intend to poll (one per queue).
- The **public URL** the browser uses to reach Servicedesk (e.g.
  `https://desk.company.com` in production, `http://localhost:5173` in dev
  where Vite runs on a separate port from Kestrel). You need it for the
  redirect URI in section 1.4 and for the `App.PublicBaseUrl` setting in
  section 4a.

## 1. Create the app registration
1. Open the [Azure Portal](https://portal.azure.com) and go to
   **Entra ID → App registrations → New registration**.
2. Name it something recognizable, e.g. **Servicedesk**. (One app covers mail
   and login — separating them is possible but rarely worth the overhead.)
3. **Supported account types:** *Accounts in this organizational directory only*.
   Servicedesk is single-tenant; multi-tenant is not supported and not needed —
   customers will get their own login flow in a later release, not M365.
4. **Redirect URI:** pick **Web** and enter
   `https://<your-public-host>/api/auth/microsoft/callback`.
   Example: `https://desk.company.com/api/auth/microsoft/callback`.
   This exact string must match what Servicedesk sends on the challenge or
   Azure returns `AADSTS50011`.
5. Click **Register**.
6. On the **Overview** page, copy:
   - **Application (client) ID**
   - **Directory (tenant) ID**

## 2. Grant API permissions

Both permission families live under **API permissions → Add a permission →
Microsoft Graph** on the same app registration.

### 2a. Application permissions (mail + directory reads)
Used by background services and by admin tools — no user signed in.

| Permission | Why |
|---|---|
| `Mail.ReadWrite` | Polling loop (read), mark-as-read, move to processed folder. `Mail.Read` alone is **not enough** — PATCH and Move both need write. |
| `Mail.Send` | Outbound mail (replies, forwards, new) via the draft-then-send pattern that captures the Graph `internetMessageId` for reply threading. |
| `User.Read.All` | Reading `accountEnabled` during M365 sign-in (deprovision check) and the directory typeahead on the Settings → Users admin UI. |

### 2b. Delegated permissions (M365 sign-in)
Used when an agent signs in via the "Sign in with Microsoft" button.

| Permission | Why |
|---|---|
| `openid` | OIDC — issues the ID token. |
| `profile` | OIDC — makes `oid` / `preferred_username` / `name` claims available. |
| `email` | OIDC — ensures the `email` claim is populated. |
| `User.Read` | Read the signed-in user's basic profile. Usually added by Azure automatically on registration. |

After adding everything, click **Grant admin consent for &lt;your tenant&gt;**.
Every row must show a green checkmark.

> If you previously granted only `Mail.Read`, symptoms are: delta polling works
> (mails are ingested into tickets) but the server log shows `Access is denied`
> for mark-as-read and folder-create, and the Health page flips the
> `mail-polling` subsystem to **Warning**.

> **Why application for Mail and delegated for login?** Mail polling runs as
> the app itself — there is no signed-in user at poll time, so application
> permissions are correct. The login flow is always on behalf of a specific
> user, so delegated permissions are correct. One app registration is allowed
> to carry both sets in parallel.

## 3. Create a client secret
1. Go to **Certificates & secrets → Client secrets → New client secret**.
2. Pick a description and an expiry (Azure caps this at 24 months). Put the
   expiry date on your own calendar — mail *and* M365 sign-in stop working when
   the secret expires.
3. Click **Add** and **immediately copy the Value** column. Azure will never
   show it again.

## 4. Configure Servicedesk

1. Sign in as an admin (local account is fine — the bootstrap admin does not
   need M365).
2. Go to **Settings → Mail → Microsoft Graph**.
3. Paste the **Tenant ID** and **Client ID** from step 1.6.
4. Paste the **Client secret** from step 3.3 and click **Save**. The secret is
   encrypted at rest (ASP.NET Core DataProtection) before it hits the database
   — plaintext never leaves the request.
5. In the **Test connection** row, enter one of your mailbox addresses and
   click **Test**. You should see `OK — <ms>`. If you see an error, see
   Troubleshooting below.

### 4a. Enable M365 sign-in (optional — can be turned on later)

1. **Set `App.PublicBaseUrl`.** Search for it in the Settings page and fill in
   the origin the browser uses to reach Servicedesk — e.g.
   `https://desk.company.com` in production, `http://localhost:5173` in dev
   where Vite runs on a different port from Kestrel. **Required** for M365 to
   work correctly when frontend and backend sit on different origins; without
   it the callback lands the browser on the Kestrel origin (`:5080`) and
   Vite's dev-server never serves the SPA. It is also the same setting the
   notification-mail CTA uses, so you had to set it eventually anyway.
2. Still on **Settings → Mail → Microsoft Graph**, scroll to the
   **Microsoft 365 sign-in** block at the bottom of that card. The toggle is
   read-only until Tenant ID / Client ID / Secret are filled.
3. Flip `Auth.Microsoft.Enabled` to `true`.
4. The redirect URI on your Azure app registration (section 1.4) must be
   `<App.PublicBaseUrl>/api/auth/microsoft/callback`. **Exactly**. Double-check
   scheme, host, port, path — Azure's string-compare is intolerant.
5. On the next page load, the login page shows a **Sign in with Microsoft**
   button below the local email/password form.
6. Go to **Settings → Users** and use **+ Add from M365** to link the first
   agent / admin. The Graph directory typeahead will appear; pick a user, pick
   a role, confirm. That user can now sign in via Microsoft.

> **First-admin note.** The first admin on a fresh install is always a local
> account (created via the setup wizard, local password + optional TOTP). That
> admin can later upgrade to M365 via **Settings → Users → ⋯ → Upgrade to
> M365**. The upgrade drops the local password + TOTP in the same transaction
> and revokes the old session — re-login is required.

## 5. Map mailboxes to queues
1. Go to **Settings → Tickets → Queues** and open the queue you want.
2. Fill in **Inbound mailbox address** (e.g. `support@company.com`).
3. Optionally set **Outbound mailbox address** — used as the `From` on outbound
   mail; falls back to the inbound address if unset.
4. Save. Within one polling interval (default: 60 seconds) the service starts
   pulling mail for that queue.

Each queue keeps its own Graph delta cursor, so a restart never reprocesses
old mail.

## Troubleshooting

### Mail polling
- **"Authorization_IdentityNotFound"** — wrong tenant ID, or the mailbox isn't
  in this tenant.
- **"Authorization_RequestDenied"** — admin consent wasn't granted, or the
  mailbox is excluded by an ApplicationAccessPolicy.
- **"invalid_client"** — the client secret is wrong, expired, or has been
  deleted in Azure.

### M365 sign-in
- **"AADSTS50011: The reply URL … does not match"** — the redirect URI on the
  app registration (step 1.4) must exactly match
  `<App.PublicBaseUrl>/api/auth/microsoft/callback` (scheme, host, port, path).
  No trailing slash. HTTPS only in production.
- **Login succeeds but the browser lands on a 404 at `:5080/`** — in a
  split-origin dev setup (Vite on `:5173`, Kestrel on `:5080`) the callback
  has nowhere to redirect the SPA unless `App.PublicBaseUrl` is set to the
  Vite origin. Set it (section 4a.1) and the Azure redirect URI (section 1.4)
  to match, then retry.
- **Login page shows "Your Microsoft account is not linked to this
  servicedesk"** — the OID coming back from Azure is not yet on any user row.
  An admin must first add the user via Settings → Users → + Add from M365.
- **Login page shows "Your Microsoft account is disabled"** — the user's
  `accountEnabled` is false in Azure. Re-enable in Azure and retry; the local
  row is also marked inactive on this rejection, so an admin must reactivate
  it in Settings → Users after fixing Azure.
- **Login page shows "The sign-in session expired"** — the single-use intent
  cookie is gone (browser cleared cookies between challenge and callback, the
  user kept the Azure tab open past the 10-minute window, or a CSRF / tampering
  attempt tripped the state check). Start the sign-in again.
- **"consent_required"** — the app registration gained a permission that
  hasn't been granted admin consent yet. Re-visit API permissions and click
  **Grant admin consent**.

## Security notes
- The client secret is stored in the `protected_secrets` table, encrypted with
  the app's DataProtection key ring. Rotate the secret in Azure and re-paste it
  here whenever it approaches expiry.
- Never commit the secret to git or paste it into an issue or chat.
- Consider narrowing the app's mailbox scope with an
  [ApplicationAccessPolicy](https://learn.microsoft.com/en-us/graph/auth-limit-mailbox-access)
  so it can only read the exact mailboxes you configure — a defensive guardrail
  against misconfigured permissions.
- For M365 sign-in, consider requiring **MFA via Conditional Access** on the
  agents who access this app. Servicedesk honours whatever MFA Azure enforces —
  a user who completes MFA in Azure arrives with an MFA'd session.
- The app registration's **supported account types** must stay **single-tenant**.
  Switching it to multi-tenant does not enable extra Servicedesk features and
  opens the login flow to any Azure tenant on the planet.
