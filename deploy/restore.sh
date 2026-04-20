#!/usr/bin/env bash
# restore.sh — restore from a backup.sh-created backup directory.
#
# Restore order is MANDATORY:
#   1. Stop the app container (not Postgres — it stays up).
#   2. Drop + recreate the target database.
#   3. pg_restore the dump.
#   4. Re-apply REVOKE UPDATE, DELETE ON audit_log (the REVOKE rides on the
#      role, not the table — a pg_restore of a fresh DB loses it).
#   5. rsync blobs back into place.
#   6. Start the app container.
#
# Blobs-after-Postgres: `ticket_events` carries blob hashes. If the app boots
# against restored rows but missing blobs, it would log IncidentLog entries
# for every open-attachment click. Not destructive — just noisy. Doing the
# blob rsync while the app is down keeps the log clean.
#
# Usage:
#   sudo /opt/servicedesk/deploy/restore.sh /var/backups/servicedesk/2026-04-20T12-34-56Z

set -euo pipefail

readonly SECRETS_FILE="/etc/servicedesk/secrets.env"
readonly BLOB_ROOT="/var/lib/servicedesk/blobs"
readonly PG_APP_DB="servicedesk"
readonly INSTALL_DIR_DEFAULT="/opt/servicedesk"

[[ $EUID -eq 0 ]] || { echo "[✗] restore.sh must run as root." >&2; exit 1; }

BACKUP_DIR="${1:-}"
[[ -n "$BACKUP_DIR" ]] || { echo "Usage: restore.sh <backup-dir>" >&2; exit 1; }
[[ -d "$BACKUP_DIR" ]] || { echo "[✗] Backup dir not found: ${BACKUP_DIR}" >&2; exit 1; }
[[ -f "${BACKUP_DIR}/servicedesk.dump" ]] || { echo "[✗] ${BACKUP_DIR}/servicedesk.dump missing." >&2; exit 1; }

INSTALL_DIR="${INSTALL_DIR:-$INSTALL_DIR_DEFAULT}"
[[ -f "$SECRETS_FILE" ]] || { echo "[✗] ${SECRETS_FILE} missing — cannot read app role." >&2; exit 1; }

conn="$(grep '^SERVICEDESK_ConnectionStrings__Postgres=' "$SECRETS_FILE" | cut -d= -f2-)"
DB_USER="$(echo "$conn" | grep -oE 'Username=[^;]+' | cut -d= -f2)"

echo "[!] RESTORE WILL REPLACE the current Postgres DB '${PG_APP_DB}' and blob"
echo "    directory ${BLOB_ROOT} with the contents of:"
echo "      ${BACKUP_DIR}"
if [[ -f "${BACKUP_DIR}/manifest.txt" ]]; then
    echo ""
    sed 's/^/      /' "${BACKUP_DIR}/manifest.txt"
    echo ""
fi
read -rp "Type 'RESTORE' to continue: " confirm </dev/tty
[[ "$confirm" == "RESTORE" ]] || { echo "[✗] Aborted."; exit 1; }

# ---- 1. stop the app (NOT nginx — users get 502 briefly, acceptable) ---
echo "[i] Stopping app container …"
(cd "${INSTALL_DIR}/deploy" && docker compose stop app) || true

# ---- 2. drop + recreate db ---------------------------------------------
echo "[i] Dropping + recreating database ${PG_APP_DB} …"
# Kill any stray connections (our app is down, but pgAdmin / psql sessions might linger).
sudo -u postgres psql -c \
    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='${PG_APP_DB}' AND pid <> pg_backend_pid();" >/dev/null
sudo -u postgres psql -c "DROP DATABASE IF EXISTS ${PG_APP_DB};"
sudo -u postgres psql -c "CREATE DATABASE ${PG_APP_DB} OWNER \"${DB_USER}\";"

# ---- 3. pg_restore ------------------------------------------------------
echo "[i] Restoring ${BACKUP_DIR}/servicedesk.dump …"
sudo -u postgres pg_restore --no-owner --role="${DB_USER}" -d "${PG_APP_DB}" "${BACKUP_DIR}/servicedesk.dump"

# ---- 4. re-apply REVOKE -------------------------------------------------
echo "[i] Re-applying audit_log REVOKE …"
sudo -u postgres psql -d "${PG_APP_DB}" -c \
    "REVOKE UPDATE, DELETE ON audit_log FROM \"${DB_USER}\";" >/dev/null
sudo -u postgres psql -d "${PG_APP_DB}" -c \
    "REVOKE ALL ON SCHEMA public FROM PUBLIC;" >/dev/null
sudo -u postgres psql -d "${PG_APP_DB}" -c \
    "GRANT USAGE, CREATE ON SCHEMA public TO \"${DB_USER}\";" >/dev/null

# ---- 5. rsync blobs -----------------------------------------------------
if [[ -d "${BACKUP_DIR}/blobs" ]]; then
    echo "[i] Restoring blobs into ${BLOB_ROOT} …"
    mkdir -p "${BLOB_ROOT}"
    # --delete IS used here: we want the blob store to exactly match the
    # backup, otherwise orphan files pile up over repeated restore tests.
    rsync -a --delete "${BACKUP_DIR}/blobs/" "${BLOB_ROOT}/"
    chown -R 10001:10001 "${BLOB_ROOT}"
    echo "[✓] Blobs restored."
else
    echo "[!] ${BACKUP_DIR}/blobs missing — leaving ${BLOB_ROOT} as is."
fi

# ---- 6. bring app back --------------------------------------------------
echo "[i] Starting app container …"
(cd "${INSTALL_DIR}/deploy" && DOMAIN="${DOMAIN:-localhost}" docker compose up -d app)

echo "[i] Waiting up to 2 min for app to become healthy …"
for ((i=1; i<=60; i++)); do
    status="$(docker inspect --format='{{.State.Health.Status}}' servicedesk-app-1 2>/dev/null || echo starting)"
    if [[ "$status" == "healthy" ]]; then
        echo "[✓] App healthy."
        break
    fi
    sleep 2
done

echo "[✓] Restore complete."
