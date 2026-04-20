#!/usr/bin/env bash
# backup.sh — full-fidelity backup of a running Servicedesk install.
#
# What it captures:
#   • Postgres: pg_dump -Fc of the 'servicedesk' database (includes schema,
#     data, AND the DataProtection keyring row — so an offline restore brings
#     cookies + antiforgery tokens back to life).
#   • Blob store: rsync of /var/lib/servicedesk/blobs. Content-addressed +
#     atomic-rename writes mean rsync during a live write captures either
#     nothing or a complete file — never half a hash.
#   • The on-disk copy of secrets.env is deliberately NOT backed up here.
#     Back it up separately to your password manager / offline vault.
#
# Output layout:
#   ${BACKUP_ROOT}/YYYY-MM-DDThh-mm-ssZ/
#       servicedesk.dump             (pg_dump -Fc)
#       blobs/                       (rsync'd bind-mount)
#       manifest.txt                 (git SHA, pg size, blob count)
#
# Usage:
#   sudo /opt/servicedesk/deploy/backup.sh              # default root
#   sudo BACKUP_ROOT=/mnt/nas/sd bash backup.sh         # custom root

set -euo pipefail

readonly SECRETS_FILE="/etc/servicedesk/secrets.env"
readonly BLOB_ROOT="/var/lib/servicedesk/blobs"
readonly PG_APP_DB="servicedesk"
readonly INSTALL_DIR_DEFAULT="/opt/servicedesk"

BACKUP_ROOT="${BACKUP_ROOT:-/var/backups/servicedesk}"
INSTALL_DIR="${INSTALL_DIR:-$INSTALL_DIR_DEFAULT}"

[[ $EUID -eq 0 ]] || { echo "[✗] backup.sh must run as root." >&2; exit 1; }
[[ -f "$SECRETS_FILE" ]] || { echo "[✗] ${SECRETS_FILE} not found — is this a Servicedesk host?" >&2; exit 1; }

# Recover DB role from secrets.env so we can run pg_dump as that role.
conn="$(grep '^SERVICEDESK_ConnectionStrings__Postgres=' "$SECRETS_FILE" | cut -d= -f2-)"
DB_USER="$(echo "$conn" | grep -oE 'Username=[^;]+' | cut -d= -f2)"
[[ -n "$DB_USER" ]] || { echo "[✗] Could not parse Username from secrets.env." >&2; exit 1; }

ts="$(date -u +"%Y-%m-%dT%H-%M-%SZ")"
dest="${BACKUP_ROOT}/${ts}"
echo "[i] Backup destination: ${dest}"
mkdir -p "${dest}/blobs"
chmod 700 "${BACKUP_ROOT}" "${dest}"

# ---- Postgres dump ------------------------------------------------------
echo "[i] Dumping Postgres database '${PG_APP_DB}' as role ${DB_USER} …"
sudo -u postgres pg_dump -Fc -d "${PG_APP_DB}" -f "${dest}/servicedesk.dump"
pg_size="$(du -h "${dest}/servicedesk.dump" | awk '{print $1}')"
echo "[✓] Postgres dump: ${pg_size}"

# ---- Blob rsync ---------------------------------------------------------
if [[ -d "$BLOB_ROOT" ]]; then
    echo "[i] Syncing blob store from ${BLOB_ROOT} …"
    # --delete is intentionally OMITTED so a partial sync never drops files
    # from the backup. Orphan files in the backup are a non-issue.
    rsync -a "${BLOB_ROOT}/" "${dest}/blobs/"
    blob_count="$(find "${dest}/blobs" -type f | wc -l)"
    blob_size="$(du -sh "${dest}/blobs" | awk '{print $1}')"
    echo "[✓] Blobs: ${blob_count} files, ${blob_size}"
else
    echo "[!] ${BLOB_ROOT} does not exist — nothing to back up."
    blob_count=0; blob_size="0"
fi

# ---- Manifest -----------------------------------------------------------
git_sha="$(git -C "$INSTALL_DIR" rev-parse HEAD 2>/dev/null || echo "unknown")"
git_ref="$(git -C "$INSTALL_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")"
cat > "${dest}/manifest.txt" <<EOF
Servicedesk backup manifest
===========================
Timestamp (UTC):  ${ts}
Host:             $(hostname -f)
Install dir:      ${INSTALL_DIR}
Git ref:          ${git_ref}
Git SHA:          ${git_sha}
Postgres db:      ${PG_APP_DB}
Postgres role:    ${DB_USER}
Dump size:        ${pg_size}
Blob count:       ${blob_count}
Blob total:       ${blob_size}

Restore with:
    sudo ${INSTALL_DIR}/deploy/restore.sh ${dest}
EOF

echo "[✓] Manifest written."
echo "[✓] Backup complete → ${dest}"
