# Servicedesk

A self-hosted helpdesk for small and mid-size teams. One install per organisation, runs on a single Ubuntu host, integrates with Microsoft 365 mail, and keeps every ticket, comment, and timeline event under your own control.

## What it does

**Ticket management**
- Tickets organised by queue, status, priority, and category. Statuses carry a `state_category` (New / Open / Pending / Resolved / Closed) so SLA logic and "is open" filters survive admins renaming display labels.
- Full-text search across subjects and bodies via PostgreSQL `tsvector` + GIN, with `pg_trgm` and `unaccent` for fuzzy matching.
- Keyset-paginated lists tuned for tens of thousands of tickets, with infinite scroll on the agent list.
- Ticket merge (re-points all events, mail, pins, notifications, and intake-form instances onto the survivor) and ticket split (carve a side-conversation out of a mail into a fresh ticket without breaking the source).
- Configurable column visibility per view, with admin defaults that cascade down to per-user overrides.
- Tiptap-based rich-text composer for ticket bodies, comments, and notes; HTML is sanitised with DOMPurify on render.

**Mail intake and outbound**
- Inbound mail polled from Microsoft Graph (delta queries, configurable interval). One Graph mailbox maps to one queue; replies thread by `internetMessageId` so external mail clients see the conversation correctly.
- Outbound is draft-then-send, so the Graph-assigned message id can be persisted before delivery for reliable threading.
- Attachments stored in a content-addressed blob store on disk; deduplication is automatic via SHA-256.
- Per-mail diagnostics page surfaces stuck attachment jobs, failed extraction, and missing blobs for one-click triage.

**Auth and access control**
- Custom session auth with Argon2id password hashing, optional TOTP 2FA, and recovery codes (encrypted at rest).
- Single-tenant Microsoft 365 OIDC sign-in for agents and admins. Customer-portal login is a separate flow (M365 sign-in never accepts customer accounts).
- Three roles from day one — Customer, Agent, Admin — with route-, API-, and row-level scoping.
- Per-queue access control for agents; admins manage who sees what.
- Anti-CSRF (double-submit cookie + header), HTTPS-only with HSTS and CSP, rate limiting on auth and abuse-prone endpoints.

**Companies and contacts**
- Companies carry codes, VAT numbers, and a many-to-many link to contacts with roles (`primary` / `secondary` / `supplier`).
- Auto-link inbound mail to the right company by domain match, with a freemail blacklist so personal addresses don't accidentally bind half the internet to one company.
- Per-company alert text with two trigger modes (on ticket create, on ticket open) so an agent can't miss a "this customer is in escalation" notice.

**Knowledge and intake**
- Intake forms — admin-defined structured forms that prefill ticket fields and capture custom answers.
- Triggers — rule-based automations that run on ticket events (with a runs log for visibility).
- Saved views and view groups so each team can curate their own ticket lists.

**Realtime and notifications**
- SignalR drives ticket presence, live list updates, and per-user mention notifications.
- @-mention syntax in comments and notes generates targeted notifications with read/ack tracking.

**Integrations**
- Microsoft Graph for mail and OIDC.
- Adsolut Accounting (Wolters Kluwer) — bidirectional sync for customers and contacts, with per-direction toggles, a hash-based echo-pull guard, and a coverage page that surfaces gaps between the local universe and Adsolut.

**Operations**
- Health-check dashboard rolls system status, mail polling, and integration checks into one tile.
- Append-only audit log with hash-chained rows for security-sensitive actions; mutation rights are revoked at install time so even the app role can't rewrite history.
- Settings page with categorised, searchable, database-backed configuration. Every tunable value (durations, thresholds, retry counts, mail templates, SLA windows) lives there and edits take effect without a restart.
- Global cmdk-style command palette (Ctrl/Cmd+K) for quick navigation across tickets, contacts, companies, and settings.

**UI**
- React 18, TanStack Router and Query, Tailwind, shadcn/ui, Framer Motion. Glassmorphism dark theme with a WebGL mesh background. Inter variable font.
- Live server time and version visible in the UI at all times — every time-sensitive operation reads the server clock, not the browser.

## Stack

- **Backend** — ASP.NET Core 8, minimal APIs grouped by route, Dapper-first with raw parameterised SQL, EF Core present but unused (the hot paths benefit from keyset pagination).
- **Frontend** — React 18 + TypeScript + Vite, bundled into the production container.
- **Database** — PostgreSQL native on the host (not in Docker; deliberate). Schema is bootstrapped idempotently on app start (`CREATE IF NOT EXISTS` / `ADD COLUMN IF NOT EXISTS`); no migration history table.
- **Realtime** — SignalR over WebSockets.
- **Mail** — Microsoft Graph, app-only auth, polling intake.
- **Reverse proxy** — Nginx in Docker with TLS via Let's Encrypt (Certbot, with a path-unit-based renewal bridge so the in-container app can request a renewal without holding host privileges).
- **Logging** — Serilog, structured, with a separate audit-log sink for security events.

## Production install

A single command on a fresh **Ubuntu 22.04 or 24.04** host (root or sudo):

```bash
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/install.sh)
```

You'll be prompted for your domain, an admin email (used for Let's Encrypt), whether to enable TLS, and a few PostgreSQL details. Sensible defaults fill in the rest. All prompts can be pre-answered via environment variables for unattended runs (see the comment block at the top of `deploy/install.sh`).

The installer is idempotent — re-running on a healthy install is safe; every step checks state before acting. Secrets are generated only on first run; an existing `/etc/servicedesk/secrets.env` is never overwritten.

### What gets installed

- **Docker Engine + Compose plugin** from Docker's official APT repo.
- **PostgreSQL** (native on the host, not containerised) plus client tools. The installer pins `listen_addresses`, configures `pg_hba.conf` for the Docker bridge subnet only, and provisions an application role + database.
- **The Servicedesk app** under `/opt/servicedesk` — repo cloned, app + Nginx containers built and started via Docker Compose, with a content-addressed blob bind-mount at `/var/lib/servicedesk/blobs`.
- **Nginx** (in Docker) as the reverse proxy with TLS termination and security headers. The installer picks the right template (HTTP-only or HTTP+HTTPS) based on your TLS choice.
- **Certbot** with a Let's Encrypt certificate when TLS is enabled, plus a `systemd` path-unit (`servicedesk-cert-renew.path`) that renews on signal from the app container — no privileged renewal cron.
- **`chrony`** if the system clock is skewed (Let's Encrypt and the database both refuse to work with a wrong clock; fresh VPS images often need a nudge).
- **`/etc/servicedesk/secrets.env`** with generated master keys (data protection, audit hash key, DB password). Mode `600`, owned by root.
- **Audit-log lockdown** — once the schema is bootstrapped, mutation rights on `audit_log` are revoked from the application role, so even a compromised app process can't rewrite history.
- **An admin-setup URL** is printed at the end. Open it in a browser to create the first admin account; the URL self-expires after first use.

The host firewall and SSH hardening are intentionally **not** modified. They are policy decisions that belong with whoever provisions the box. Open ports `80` and `443` (and `22` for your own access) on whatever firewall you already run.

Full walkthrough, prerequisites, and troubleshooting in [`docs/deployment-runbook.md`](docs/deployment-runbook.md). Microsoft Graph configuration is documented separately in [`docs/microsoft-graph-setup.md`](docs/microsoft-graph-setup.md).

## Update

Same one-liner pattern. Always offers a pre-update backup, and auto-rolls-back if the new version doesn't come up healthy:

```bash
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/update.sh)
```

## Backup and restore

```bash
sudo /opt/servicedesk/deploy/backup.sh
sudo /opt/servicedesk/deploy/restore.sh /var/backups/servicedesk/<timestamp>
```

The backup script captures the PostgreSQL dump and the blob store together, so a restore is always self-consistent. Cadence advice and a disaster-recovery checklist are in [`docs/backup-runbook.md`](docs/backup-runbook.md).

## Local development

Dev runs bare-metal — no Docker required. PostgreSQL native on Windows, macOS, or Linux; ASP.NET Core on Kestrel; Vite on its own port with a proxy to the API.

```bash
# 1. Install PostgreSQL natively and create a dev DB + role.
#    The schema itself is bootstrapped automatically on first run.

# 2. Set the required dev secrets via user-secrets (NOT .env — that's production-only):
dotnet user-secrets --project src/Servicedesk.Api set "ConnectionStrings:Postgres" "Host=localhost;Database=servicedesk_dev;Username=sd_dev;Password=..."
dotnet user-secrets --project src/Servicedesk.Api set "Audit:HashKey" "$(openssl rand -base64 32)"
dotnet user-secrets --project src/Servicedesk.Api set "DataProtection:MasterKey" "$(openssl rand -base64 32)"

# 3. Run the backend on :5080:
dotnet run --project src/Servicedesk.Api

# 4. In a second terminal, run the frontend on :5173 (with /api proxy to :5080):
cd src/Servicedesk.Web
npm install
npm run dev
```

Open `http://localhost:5173`. The Vite proxy forwards `/api/*` and `/hubs/*` to Kestrel.

## License

TBD.
