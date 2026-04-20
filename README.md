# Servicedesk

A self-hosted servicedesk app. Built because the existing options were either ugly, slow, expensive, or all three.

## What this is

A small project to build a servicedesk that's actually nice to use. Dark mode, glass, no clutter. Runs on your own box. That's the whole pitch.

## Stack

- ASP.NET Core 8 · React · TypeScript · Vite · Tailwind CSS · shadcn/ui
- PostgreSQL (native on host) · Dapper for hot paths, EF Core where convenient
- Microsoft 365 for mail (Graph polling) + M365 sign-in (OIDC, agents/admins only)
- SignalR for ticket presence + live updates
- Docker Compose behind Nginx, TLS via Let's Encrypt

## Production install

One command on a fresh Ubuntu 22.04 / 24.04 host:

```bash
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/install.sh)
```

You'll be prompted for your domain, whether to enable Let's Encrypt, and a few Postgres details. The script handles everything else — Docker install, Postgres provisioning, nginx template, cert issuing, audit-log lockdown. When it finishes it prints a setup URL; open it in a browser to create the first admin account.

Full walkthrough + troubleshooting in [`docs/deployment-runbook.md`](docs/deployment-runbook.md).

## Update

Same one-liner pattern. Always offers a pre-update backup, and auto-rolls-back if the new version doesn't come up healthy:

```bash
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/update.sh)
```

## Backup & restore

```bash
sudo /opt/servicedesk/deploy/backup.sh
sudo /opt/servicedesk/deploy/restore.sh /var/backups/servicedesk/<timestamp>
```

Full cadence advice + disaster-recovery checklist in [`docs/backup-runbook.md`](docs/backup-runbook.md).

## Local development

Dev runs bare-metal — no Docker needed. Postgres native on Windows/macOS/Linux, ASP.NET Core on Kestrel, Vite on its own port with proxy to the API.

```bash
# 1. Install PostgreSQL natively and create a dev DB + role.
#    (The schema itself is auto-bootstrapped by DatabaseBootstrapper on first run.)

# 2. Set the required dev secrets via user-secrets (NOT .env — that's production-only):
dotnet user-secrets --project src/Servicedesk.Api set "ConnectionStrings:Postgres" "Host=localhost;Database=servicedesk_dev;Username=sd_dev;Password=..."
dotnet user-secrets --project src/Servicedesk.Api set "Audit:HashKey" "$(openssl rand -base64 32)"
dotnet user-secrets --project src/Servicedesk.Api set "DataProtection:MasterKey" "$(openssl rand -base64 32)"

# 3. Run the backend (:5080):
dotnet run --project src/Servicedesk.Api

# 4. In a second terminal, run the frontend (:5173 with /api proxy to :5080):
cd src/Servicedesk.Web
npm install
npm run dev
```

Open `http://localhost:5173`. The Vite proxy forwards `/api/*` and `/hubs/*` to Kestrel.

## License

TBD.
