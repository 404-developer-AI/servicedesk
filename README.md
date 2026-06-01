# Servicedesk

Self-hosted helpdesk for small and mid-size teams. One install per organisation, runs on a single Ubuntu host, integrates with Microsoft 365 mail. Tickets, comments, attachments, and audit history stay on your own box.

## Stack

- **Backend** — ASP.NET Core 8 (C#), Dapper-first with raw parameterised SQL on hot paths, EF Core for CRUD.
- **Frontend** — React 18 + TypeScript + Vite, bundled into the production container.
- **Database** — PostgreSQL 16, native on the host (not Docker — deliberate). Schema is bootstrapped idempotently on app start.
- **Realtime** — SignalR over WebSockets (ticket presence, live list updates, mention notifications).
- **Mail** — Microsoft Graph, app-only auth, polling intake. Outbound is draft-then-send so the Graph `internetMessageId` is persisted for reply-threading.
- **Auth** — custom session auth (Argon2id, optional TOTP 2FA, encrypted recovery codes) plus single-tenant Microsoft 365 OIDC for agents/admins. Customer-portal login is a separate flow.
- **Reverse proxy** — Nginx (in Docker) with TLS via Let's Encrypt (Certbot).
- **UI** — Tailwind + shadcn/ui + Framer Motion. Dark glassmorphism by default, light theme opt-in. Inter variable font.
- **Search** — PostgreSQL `tsvector` + GIN with `pg_trgm` and `unaccent` for fuzzy matching; global Ctrl/Cmd+K palette across tickets, contacts, companies, and settings.

## Requirements

- **OS** — Ubuntu **24.04 LTS** (Noble). The one-liner depends on PostgreSQL 16 being available in the default apt repos, which is only true from 24.04 onwards. On Ubuntu 22.04 the installer will fail at `apt-get install postgresql-16` unless you add the PGDG repo by hand first.
- **Architecture** — `x86_64` / `amd64`.
- **Privileges** — root, or a user with passwordless `sudo`.
- **Network** — public DNS A-record pointing at the host (Let's Encrypt requirement). Inbound `80` and `443` reachable.
- **Domain + admin email** — collected at install-time (the email is the Let's Encrypt account contact).
- **Microsoft 365 tenant** — optional, only for Graph mail intake and OIDC sign-in. Can be configured post-install.

## Install

One command on a fresh host:

```bash
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/install.sh)
```

You'll be prompted for the domain, an admin email, whether to enable TLS, and a few PostgreSQL details. Sensible defaults fill in the rest. All prompts can be pre-answered via environment variables for unattended runs (see the comment block at the top of `deploy/install.sh`).

The installer is idempotent — re-running on a healthy install is safe; every step checks state before acting. Secrets are generated only on first run; an existing `/etc/servicedesk/secrets.env` is never overwritten.

### What gets installed

- **Docker Engine + Compose plugin** from Docker's official APT repo.
- **PostgreSQL 16** (native, host-side) plus client tools. The installer pins `listen_addresses`, locks `pg_hba.conf` to the Docker bridge subnet, and provisions an application role + database.
- **The app** under `/opt/servicedesk` — repo cloned, app + Nginx containers built and started via Docker Compose, with a content-addressed blob bind-mount at `/var/lib/servicedesk/blobs`.
- **Nginx** (in Docker) as the reverse proxy with TLS termination and security headers.
- **Certbot** + Let's Encrypt when TLS is enabled, plus a `systemd` path-unit (`servicedesk-cert-renew.path`) so the in-container app can trigger renewals without holding host privileges.
- **`chrony`** if the system clock is skewed (fresh VPS images often need a nudge before Let's Encrypt will issue).
- **`/etc/servicedesk/secrets.env`** with generated master keys (data-protection, audit hash key, DB password). Mode `600`, root-owned.
- **Audit-log lockdown** — once the schema is bootstrapped, `UPDATE`/`DELETE` on `audit_log` is revoked from the application role.
- **An admin-setup URL** printed at the end of the run. Open it in a browser to create the first admin account; the URL self-expires after first use.

The host firewall and SSH hardening are intentionally **not** touched — they are policy decisions that belong with whoever provisions the box. Open ports `80` and `443` (and `22` for your own access) on whatever firewall you already run.

Full walkthrough, prerequisites, and troubleshooting: [`docs/deployment-runbook.md`](docs/deployment-runbook.md). Microsoft Graph configuration: [`docs/microsoft-graph-setup.md`](docs/microsoft-graph-setup.md).

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

The backup script captures the PostgreSQL dump and the blob store together, so a restore is always self-consistent. Cadence advice and disaster-recovery checklist: [`docs/backup-runbook.md`](docs/backup-runbook.md).

## Local development

Dev runs bare-metal — no Docker required. PostgreSQL native on Windows, macOS, or Linux; ASP.NET Core on Kestrel; Vite on its own port with a proxy to the API.

```bash
# 1. Install PostgreSQL natively and create a dev DB + role.
#    Schema is bootstrapped automatically on first run.

# 2. Set the dev secrets via user-secrets (not .env — that's production-only):
dotnet user-secrets --project src/Servicedesk.Api set "ConnectionStrings:Postgres" "Host=localhost;Database=servicedesk_dev;Username=sd_dev;Password=..."
dotnet user-secrets --project src/Servicedesk.Api set "Audit:HashKey"             "$(openssl rand -base64 32)"
dotnet user-secrets --project src/Servicedesk.Api set "DataProtection:MasterKey"  "$(openssl rand -base64 32)"

# 3. Run the backend on :5080
dotnet run --project src/Servicedesk.Api

# 4. In a second terminal, run the frontend on :5173 (with /api proxy to :5080)
cd src/Servicedesk.Web
npm install
npm run dev
```

Open `http://localhost:5173`. The Vite proxy forwards `/api/*` and `/hubs/*` to Kestrel.

## Security

- Parameterised SQL only (no string concatenation).
- Argon2id password hashing, optional TOTP 2FA, encrypted recovery codes.
- Anti-CSRF (double-submit cookie + header), HSTS, CSP, rate limiting on auth and abuse-prone endpoints.
- Sensitive fields encrypted at rest via ASP.NET Data Protection.
- Append-only, hash-chained audit log; mutation rights revoked from the app role at install time.

## License

Licensed under the Apache License, Version 2.0 — see [LICENSE](LICENSE).

Provided as is, without warranty or liability of any kind. See [SECURITY.md](SECURITY.md)
for how to report a vulnerability.
