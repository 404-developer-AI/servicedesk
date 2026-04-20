# Backup & restore runbook

Two scripts under `deploy/`: `backup.sh` (captures) and `restore.sh` (replaces). Neither touches `secrets.env` — that lives in your password manager, not in rsync output.

## What a backup contains

Each backup is a timestamped directory under `/var/backups/servicedesk/` (override via `BACKUP_ROOT`):

```
/var/backups/servicedesk/2026-04-20T12-34-56Z/
├── servicedesk.dump      # pg_dump -Fc (custom format, zstd-compressed)
├── blobs/                # rsync'd /var/lib/servicedesk/blobs
└── manifest.txt          # timestamp, git SHA, sizes, restore command
```

`pg_dump -Fc` covers schema + data + the DataProtection keyring row. An offline restore brings cookies + antiforgery tokens back to life — nobody has to log in again on their trusted devices after a restore.

Blobs are content-addressed (SHA-256) + written with atomic rename. Rsync captures either nothing or a complete file, never half a hash. No application freeze needed.

`secrets.env` is deliberately NOT in the backup. Its values are:
- The DB password (also in `pg_roles`, so a DB dump already has the role's scram-sha-256 hash — you only need the plaintext to run the app against a NEW cluster).
- The Audit `HashKey` (HMAC secret for the audit chain).
- The DataProtection `MasterKey` (AEAD AD-bound key that wraps the Postgres keyring).

Losing `secrets.env` without a backup = the DataProtection keyring becomes unreadable = all sessions invalidated, all encrypted-at-rest data (TOTP secrets, recovery codes) lost. Store it in a password manager or an offline vault.

## Running a backup manually

```bash
sudo /opt/servicedesk/deploy/backup.sh
```

Output lands in `/var/backups/servicedesk/<timestamp>/`. Override:

```bash
sudo BACKUP_ROOT=/mnt/nas/servicedesk /opt/servicedesk/deploy/backup.sh
```

Typical sizes: the DB dump is roughly 2–5% of the raw DB size because `pg_dump -Fc` compresses. Blobs are already on disk — rsync dedup via hardlink is a future improvement.

## Automating backups with cron

```bash
sudo tee /etc/cron.daily/servicedesk-backup > /dev/null <<'EOF'
#!/bin/sh
/opt/servicedesk/deploy/backup.sh >> /var/log/servicedesk-backup.log 2>&1
# Rotate: keep the last 14 daily backups.
find /var/backups/servicedesk -maxdepth 1 -mindepth 1 -type d -mtime +14 -exec rm -rf {} \;
EOF
sudo chmod 755 /etc/cron.daily/servicedesk-backup
```

For weekly full + daily incremental, use `rsync --link-dest` (hardlinks unchanged blobs across timestamp dirs so only the delta costs disk). Not yet wired into `backup.sh` — request in `v0.1.x` if you need it.

## Restoring

Restore order is NOT optional: Postgres first, blobs second. Doing it the other way round gives the live app a window where `ticket_events` has blob references but the files aren't there yet — the lightbox will return 404s and `incident_log` will fill up with `blob.missing` rows. Not destructive, just noise.

```bash
sudo /opt/servicedesk/deploy/restore.sh /var/backups/servicedesk/2026-04-20T12-34-56Z
```

The script:
1. Prints the backup manifest and requires you to type `RESTORE` to confirm.
2. Stops ONLY the app container — nginx stays up so visitors see a 502 instead of a connection refused.
3. Kills lingering Postgres sessions to the target DB, drops + recreates it.
4. `pg_restore --no-owner --role=<db_user> -d servicedesk`.
5. Re-applies `REVOKE UPDATE, DELETE ON audit_log` + `REVOKE ALL ON SCHEMA public FROM PUBLIC` + `GRANT USAGE, CREATE ON SCHEMA public TO <db_user>` — these live on the role, not the table, so a fresh DB loses them.
6. Rsync `/backup/blobs/` → `/var/lib/servicedesk/blobs/` with `--delete` so the filesystem matches the backup exactly.
7. Starts the app container, waits for healthy.

## Restore-drill (quarterly)

Every backup has to be proven restorable. Suggested cadence:
1. Take a fresh backup on the production host.
2. `scp` the dir to a staging VM.
3. Run `install.sh` with `SSL=no` on the staging VM.
4. `restore.sh` against the scp'd backup dir.
5. Verify in browser: log in as admin, open a recent ticket, click an attachment from the timeline, run a global search.

This proves Postgres + blobs + DataProtection all survived the round-trip. Schedule it in your calendar — silent-backup-corruption is the classic production-ops failure mode.

## Disaster-recovery checklist

You have **three artefacts** for a full rebuild:
- Latest backup (`/var/backups/servicedesk/<ts>/`).
- `secrets.env` (password manager).
- DNS control (domain points to the new host).

Rebuild:
1. Spin up a fresh Ubuntu 24.04.
2. Run `install.sh` → fresh DB + role + secrets.env.
3. `scp` the backup dir to the new host.
4. `install -m 600 -o root -g root /from/password-manager/secrets.env /etc/servicedesk/secrets.env` (overwrite the freshly-generated one so the DataProtection master key matches the backed-up keyring).
5. `restore.sh <backup-dir>`.
6. Smoke-test in browser.

Total time: ~15 min on a decent VPS, dominated by the `npm ci` + `dotnet publish` during the image build.
