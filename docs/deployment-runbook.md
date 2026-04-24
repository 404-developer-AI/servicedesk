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
5. **Set the display timezone**: Settings → General → **Localization**. Pick an IANA zone (e.g. `Europe/Brussels`). Empty = fall back to the container's `TZ` env-var (auto-detected from the host during install). The "Currently resolved" subblock shows the zone + UTC offset the server is actually returning so you can verify without a refresh. This drives the sidebar clock, audit-log timestamps, and every timeline — SLA and business-hours calculations keep using their own per-schema zone and are unaffected.
6. **Review security-activity thresholds**: Settings → Health → **Security activity monitoring** (collapsible card). Defaults are intentionally non-alarmist (login_failed=10/h, csrf_rejected=5/h, rate_limited=50/h, login_locked_out=3/h, microsoft_login_rejected=5/h; Critical at 3× threshold). Tune down on quiet installs that want early warning, up on public-portal installs that legitimately see higher login-failure volume. `Acknowledge` acts as "counter reset" — subsequent ticks only count post-ack events, so a sustained attack still re-fires when fresh events cross the threshold.

### Configuration files

Two env-files sit on the host. They are separate on purpose: one is secret-grade, the other is free to back up in cleartext.

| Path | Mode | Contains | Backup OK? |
|---|---|---|---|
| `/etc/servicedesk/secrets.env` | 600 root:root | `SERVICEDESK_ConnectionStrings__Postgres`, `SERVICEDESK_Audit__HashKey`, `SERVICEDESK_DataProtection__MasterKey`, Graph client-secret, any other credential | **No — never copy in cleartext.** |
| `/etc/servicedesk/env.conf` | 644 root:root | `TZ`, `SERVICEDESK_TlsCert__Domain`, `APP_LE_EMAIL`, `APP_INSTALL_DIR`, `SERVICEDESK_AllowedHosts` | Yes — non-secret by design. |

Both files are loaded by the `app` service in `docker-compose.yml` (`env_file:` list). `env.conf` is also read by the host-side `servicedesk-cert-renew.service` via `EnvironmentFile=` — that's how the renewal helper gets `DOMAIN` + `APP_LE_EMAIL` without parsing anything.

`install.sh` and `update.sh` both do **append-only backfill** on `env.conf`: missing keys get added, existing keys are never overwritten. Safe to edit by hand between updates.

#### Host-header pinning (`SERVICEDESK_AllowedHosts`)

`install.sh` writes this key automatically with the single domain you entered. It pins Kestrel's `Host:` header validation — requests with a `Host` header that doesn't match are rejected before the request hits any controller. This blocks Host-header injection if nginx is ever bypassed (SSRF, misconfigured reverse proxy).

Override scenarios (edit `/etc/servicedesk/env.conf`, then `docker compose restart app`):

```bash
# CDN terminates TLS at a different hostname and forwards to your origin:
SERVICEDESK_AllowedHosts=desk.example.com;origin.example.com

# Multi-hostname install (vanity domain + default):
SERVICEDESK_AllowedHosts=help.example.com;support.example.com

# Internal-only install reached by IP (not recommended, dev only):
SERVICEDESK_AllowedHosts=*
```

Semicolon-separated. Wildcard `*` reverts to the dev-friendly default and disables Host-header validation — only do this behind a trusted internal network.

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

### TLS card stays on "monitoring disabled" after adding a domain
The `tls-cert` subsystem is keyed on `SERVICEDESK_TlsCert__Domain` in `env.conf`. If you filled a domain during install but the card still reports "monitoring disabled":

```bash
# Confirm the key is present:
grep TlsCert /etc/servicedesk/env.conf

# If missing or wrong, edit it, then restart:
docker compose -f /opt/servicedesk/deploy/docker-compose.yml restart app
```

The reader parses `/etc/letsencrypt/live/<domain>/fullchain.pem`. If that path doesn't exist (SSL=no install, or cert never issued), the card flips to Warning with "Certificate not found" rather than "monitoring disabled" — that signals the domain is configured but the cert isn't there yet.

### TLS card flips to Warning even though the cert was just renewed
Check cert-file read permission for uid 10001 (the app container user):

```bash
docker exec servicedesk-app-1 ls -la /etc/letsencrypt/live/<domain>/fullchain.pem
```

`fullchain.pem` should be mode 644 (world-readable). Let's Encrypt sets that by default; only `privkey.pem` is 600. If perms drifted (custom symlinks, manual copy), restore the mode and restart the app.

### Security-activity card stays on "Waiting for first evaluation cycle…"
The monitor ticks every `Health.SecurityActivity.IntervalSeconds` (default 60s). On a fresh boot the card shows the waiting message until the first tick completes. If it persists beyond 2 minutes:

- Check `Health.SecurityActivity.Enabled` at `/settings/health` — `false` forces Ok + "Disabled" detail, never populates the counts.
- Check the app logs for an exception in `SecurityActivityMonitor.TickAsync` (usually a DB-read failure on `audit_log`).
- Drop `IntervalSeconds` to 10s temporarily to force a fast first tick, then restore.

### Security-activity keeps firing toasts on normal traffic
Thresholds are too low for your install's baseline. The upward-transition guard means each attack-episode triggers at most one Warning + one Critical toast — if you're seeing more, the card is genuinely crossing Ok→Warning repeatedly. Pull the 1h audit-log counts:

```sql
SELECT event_type, count(*)
FROM audit_log
WHERE utc > now() - interval '1 hour'
  AND event_type IN (
    'login_failed',
    'login_locked_out',
    'csrf_rejected',
    'rate_limited',
    'auth.microsoft.login.rejected_unknown',
    'auth.microsoft.login.rejected_disabled',
    'auth.microsoft.login.rejected_customer',
    'auth.microsoft.login.rejected_inactive',
    'auth.microsoft.login.failed_callback'
  )
GROUP BY event_type;
```

Raise the matching threshold at `/settings/health` → Security activity monitoring until the baseline sits comfortably below it. No restart needed — the monitor re-reads settings each tick.

---

## 5. TLS certificate renewal

Renewals are admin-triggered from the Health page — there's no host cron and no certbot sidecar.

### Normal flow

1. Open Settings → Health. The **TLS certificate** card shows the current cert's `Expires` and `Days remaining`. The card flips amber at <14 days and red at <7 days (or expired).
2. Click **Renew now**. Confirm the prompt.
3. The card updates within a second to "Last renew attempt: running …". After certbot finishes (typically 5–15 s) the line flips to "success" or "failed".
4. On success, nginx is HUP'd in-place — no dropped connections, no manual restart.

### What runs under the hood

The app drops `/var/lib/servicedesk/cert-renew/renew.request`. A `servicedesk-cert-renew.path` systemd unit watches the file (inotify `IN_CLOSE_WRITE`) and triggers the oneshot service that runs `docker compose run --rm --entrypoint certbot certbot certonly --webroot …` — same `certs`-profile container the installer used for first-issue, just with the webroot challenge so nginx keeps serving traffic. On success the helper sends `SIGHUP` to the nginx container; on failure the helper writes the error tail to `renew.status` for the Health card.

### Inspecting a failed renewal

```bash
# Live view of the helper's output:
journalctl -fu servicedesk-cert-renew

# Most-recent attempt (status file in the bind mount):
cat /var/lib/servicedesk/cert-renew/renew.status

# Full certbot log inside the container:
docker compose -f /opt/servicedesk/deploy/docker-compose.yml run --rm --entrypoint sh certbot \
    -c "tail -n 200 /var/log/letsencrypt/letsencrypt.log"
```

Common failure causes:
- **A-record drifted off this host** → certbot's webroot challenge can't be fetched. Fix DNS, retry from the Health page.
- **nginx :80 not serving** `/.well-known/acme-challenge/` → check `docker logs servicedesk-nginx-1` and confirm the active template is `default.conf.template` (not the http-only variant — that one omits the ACME location).
- **Let's Encrypt rate-limited** (5 renewals / week / cert) → wait it out; manual renewal isn't urgent until <7 days remain.

### Bypassing the bridge (manual renewal)

If the systemd unit itself is broken and you need to renew anyway:

```bash
cd /opt/servicedesk/deploy
DOMAIN=<your-domain> docker compose run --rm --entrypoint certbot certbot \
    certonly --webroot -w /var/www/certbot -d <your-domain> \
    --email <ops-email> --agree-tos --non-interactive --no-eff-email
docker kill -s HUP servicedesk-nginx-1
```

### Migrating from pre-v0.0.18 manual cron renewal

Installs created before v0.0.18 used a host-side cron entry (typically `@daily certbot renew` or a custom wrapper) to renew the Let's Encrypt cert. From v0.0.18 onward, renewal runs through the UI-triggered systemd.path bridge — the cron entry is no longer needed.

**Migration is safe to defer.** The systemd.path unit and an existing cron don't interfere with each other — certbot itself is idempotent and Let's Encrypt will short-circuit a renewal that's not yet due (<30 days to expiry). Leave both paths running through the first post-upgrade renewal cycle if you want a safety net.

**Steps (after `update.sh` has run at least once on the new version):**

1. Confirm the bridge is active:

   ```bash
   systemctl status servicedesk-cert-renew.path
   # Expect: active (waiting) — Condition: start condition met
   ```

2. Trigger a test-renewal from **Settings → Health → TLS certificate → Renew now**. Watch the card flip to "running" and then "success" within ~15 s. Certbot will return "Certificate not yet due for renewal" if you're still >30 days out; that counts as a successful dry-run of the bridge.

3. Remove the old cron entry. Common locations:

   ```bash
   # Root crontab:
   crontab -l | grep -i certbot
   crontab -e                    # delete the certbot line

   # Cron.d / cron.daily drop-ins (check all three):
   ls /etc/cron.d/ | grep -i certbot
   ls /etc/cron.daily/ | grep -i certbot
   ls /etc/cron.weekly/ | grep -i certbot

   # Certbot's own systemd timer (if installed via apt):
   systemctl list-timers | grep certbot
   systemctl disable --now certbot.timer     # only if found
   ```

4. Verify nothing else is still scheduled to touch `/etc/letsencrypt`:

   ```bash
   grep -rI letsencrypt /etc/cron.* /etc/systemd/system/ /var/spool/cron/ 2>/dev/null
   ```

Your only remaining renewal path should now be the `servicedesk-cert-renew.path` unit, triggered either by an admin click or by a future scheduled-renewal feature (not shipped yet — manual for now).

### Disabling the bridge

If for some reason you want to revert to manual / cron-based renewal:

```bash
systemctl disable --now servicedesk-cert-renew.path
```

The Health card's "Renew now" button still POSTs the renew request — the request file just sits unprocessed. The card will still show the live `Days remaining` based on the cert file directly.
