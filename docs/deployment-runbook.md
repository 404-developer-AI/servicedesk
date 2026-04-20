# Deployment runbook

Step-by-step for installing, updating, and rolling back a Servicedesk production install. The target host is always Ubuntu 22.04 or 24.04 LTS. All commands run as root.

---

## 1. Fresh install (one-liner)

### Prerequisites
- Ubuntu 22.04 or 24.04 server, root access, internet-facing.
- A domain (only needed for TLS — if you pick `SSL=no` you can install on a private IP or VPN-only host).
- The domain's A/AAAA record already points to this host **before** running the installer — Let's Encrypt validates on port 80 during the first-issue step.

### Command

```bash
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/install.sh)
```

The script is tty-safe: `bash <(curl …)` keeps stdin attached to your terminal so interactive prompts work. Piping with `curl | bash` will NOT work because prompts get consumed by curl.

### Prompts

| Prompt | What it controls | Accept default? |
|---|---|---|
| Enable TLS via Let's Encrypt? | If yes, Certbot issues a cert on first run. If no, nginx serves only `:80`. | Production: yes. Dev demo: no. |
| Public domain | Used for both the Let's Encrypt cert and the `server_name` directive in nginx. | Type your real hostname. |
| Email for Let's Encrypt | Goes on the cert registration. Let's Encrypt emails 20 days before expiry. | Use an ops address. |
| Postgres app role | The DB role the app connects as. Used in `secrets.env` + `pg_hba.conf`. | `servicedesk_app` unless you have a convention. |
| App-database password | Enter blank → `openssl rand` generates a 32-char password. Or type your own. | Blank (auto-generate) — stored in `secrets.env`. |

### Non-interactive install

For CI / automation:

```bash
DOMAIN=sd.example.com \
EMAIL=ops@example.com \
SSL=yes \
DB_USER=servicedesk_app \
DB_PASSWORD="" \
CONFIRM=Y \
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/install.sh)
```

### What the script does

1. Verifies Ubuntu 22.04/24.04 + root.
2. Installs Docker + compose-plugin if absent.
3. Installs PostgreSQL 16 if absent, configures `listen_addresses` for the docker-gateway IP (172.17.0.1), adds a `pg_hba.conf` line for the 172.17.0.0/16 bridge (scram-sha-256).
4. Creates the Postgres role + `servicedesk` database (idempotent — separate psql calls because `CREATE DATABASE` can't run in a transaction).
5. Generates `/etc/servicedesk/secrets.env` (mode 600 root:root) with the three required `SERVICEDESK_` env-vars. Skips this if the file already exists.
6. Clones the repo into `/opt/servicedesk` (override via `INSTALL_DIR`).
7. Picks the right nginx template based on SSL yes/no.
8. Builds the image + starts the app container, waits for healthy.
9. Applies `REVOKE UPDATE, DELETE ON audit_log FROM <role>` — the hash-chain invariant can then only be broken by direct superuser access.
10. First-issue Let's Encrypt (standalone mode on :80) if SSL=yes.
11. Starts nginx.
12. Writes `/root/servicedesk-install-summary.txt` (mode 600) with the setup URL, DB credentials, and a delete-reminder. Prints the same content to stdout.

### After install

1. Open `https://<domain>/setup` in a browser → run the setup wizard to create the first admin account.
2. Copy the contents of `/root/servicedesk-install-summary.txt` to a password manager.
3. Securely delete the summary file: `shred -u /root/servicedesk-install-summary.txt`.
4. Bookmark `https://<domain>/settings/health` — it's the admin-only dashboard for every background subsystem.

---

## 2. Update a running install (one-liner)

```bash
bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/update.sh)
```

### What it does

1. Preflight: confirms `/etc/servicedesk/secrets.env` + `/opt/servicedesk/.git` exist. Recovers the DB role from secrets.env.
2. Prompts to run `backup.sh` first (default Y — say yes, the extra 30 seconds is worth it).
3. Snapshots the current git SHA for rollback.
4. `git fetch --tags` + checkout the latest `v*` tag (or `REPO_REF=…` override).
5. Diffs `.env.example` against `secrets.env` for new `SERVICEDESK_*` vars; prompts for each one.
6. `docker compose build app` + stop/start.
7. Waits 120 s for the new container to become healthy.
8. **On health-fail → automatic rollback**: checks out the previous SHA, rebuilds, starts. You end up back on the version you started from; the failing version's container is gone.
9. Reloads nginx only if `deploy/nginx/*` changed between the two refs.
10. Re-applies the `REVOKE UPDATE, DELETE ON audit_log` (idempotent — safe even on a no-op update).
11. Prints a summary with the git range + landed commits.

### Update options

```bash
# Skip the backup prompt (not recommended — only for CI testing):
SKIP_BACKUP=1 bash <(curl -sSL .../update.sh)

# Target a specific tag instead of latest:
REPO_REF=v0.0.14 bash <(curl -sSL .../update.sh)

# Full non-interactive (CI):
NO_PROMPT=1 REPO_REF=v0.0.15 bash <(curl -sSL .../update.sh)
```

---

## 3. Manual rollback

If the automatic rollback already fired (health-check failed), you're already on the previous version. Just investigate the container logs:

```bash
docker logs servicedesk-app-1 --tail 200
```

If you need to roll back a version that DID come up healthy but is still broken (e.g. a logic regression caught only in production), re-run the updater pointed at the previous SHA:

```bash
# Find the previous release tag:
git -C /opt/servicedesk tag -l 'v*' | sort -V | tail -5

# Run the updater with it as the target:
REPO_REF=v0.0.14 bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/update.sh)
```

This follows the same flow as a normal update, including a fresh backup of the broken-but-running DB (recommended — the regression may have written bad rows you want to inspect later).

---

## 4. Troubleshooting

### `nginx` won't start — "SSL certificate not found"
Let's Encrypt cert failed during install. Check the install log output — typical causes:
- A-record doesn't point to this host.
- Port 80 was blocked by a firewall during standalone-mode issuing.
- Rate-limit on the Let's Encrypt endpoint (5 certs / week / domain).

Fix: solve the root cause, then re-run just the certbot step:

```bash
cd /opt/servicedesk/deploy
docker compose run --rm --service-ports --entrypoint certbot certbot \
    certonly --standalone -d <DOMAIN> --email <EMAIL> --agree-tos --non-interactive
docker compose up -d nginx
```

### App container crash-loops on boot
Check the retry-loop messages in `docker logs servicedesk-app-1`. Common causes:
- Postgres not reachable from the container. Verify `host.docker.internal` resolves to the host gateway: `docker exec servicedesk-app-1 getent hosts host.docker.internal`.
- `postgresql.conf` missing `listen_addresses = 'localhost,172.17.0.1'`. Check with `sudo -u postgres psql -tAc "SHOW listen_addresses"`.
- `pg_hba.conf` missing the docker-bridge host rule (`host servicedesk <role> 172.17.0.0/16 scram-sha-256`).

### "audit_log REVOKE" appears after every update
Expected. It's idempotent; re-applying a REVOKE that's already in place is a no-op.

### DataProtection-keyring errors at boot
This means Postgres is unreachable and DataProtection couldn't read the keyring. Retry-loop in `DatabaseBootstrapper` tolerates a slow-starting Postgres (2-min deadline). If it exceeds that, Postgres is genuinely offline — `systemctl status postgresql`.
