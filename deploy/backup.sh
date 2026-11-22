#!/usr/bin/env bash
# backup.sh — full-fidelity backup of a running Servicedesk install.
#
# What it captures:
#   • Postgres: pg_dump -Fc of the 'servicedesk' database (includes schema,
#     data, AND the DataProtection keyring row — so an offline restore brings
#     cookies + antiforgery tokens back to life).
#   • Blob store: rsync of /var/lib/servicedesk/blobs (without .tmp/), with
#     unchanged files hardlinked against the previous backup. Content-addressed +
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
#   sudo BACKUP_RETAIN=30 backup.sh                     # keep newest 30 (default 14, 0 = never prune)
#   sudo BACKUP_MIN_FREE_PERCENT=15 backup.sh           # keep 15 % of the disk free (default 10)
#
# Before writing anything it checks the target disk has room for the dump
# plus the blobs the previous backup does not have yet, with
# BACKUP_MIN_FREE_PERCENT of the disk left over afterwards (on a default
# install PostgreSQL lives on the same disk and must never run out). Too
# little room → it stops without writing, so update.sh stops too. A run that
# fails halfway removes its incomplete directory.

set -euo pipefail

readonly SECRETS_FILE="/etc/servicedesk/secrets.env"
readonly BLOB_ROOT="/var/lib/servicedesk/blobs"
readonly PG_APP_DB="servicedesk"
readonly INSTALL_DIR_DEFAULT="/opt/servicedesk"

BACKUP_ROOT="${BACKUP_ROOT:-/var/backups/servicedesk}"
INSTALL_DIR="${INSTALL_DIR:-$INSTALL_DIR_DEFAULT}"
BACKUP_RETAIN="${BACKUP_RETAIN:-14}"
BACKUP_MIN_FREE_PERCENT="${BACKUP_MIN_FREE_PERCENT:-10}"

# Glob matching the timestamp directory names this script creates.
readonly TS_GLOB='[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]-[0-9][0-9]-[0-9][0-9]Z'

[[ "$BACKUP_RETAIN" =~ ^[0-9]+$ ]] || { echo "[✗] BACKUP_RETAIN must be a non-negative integer (got '${BACKUP_RETAIN}')." >&2; exit 1; }
[[ "$BACKUP_MIN_FREE_PERCENT" =~ ^[0-9]+$ ]] && (( BACKUP_MIN_FREE_PERCENT <= 90 )) \
    || { echo "[✗] BACKUP_MIN_FREE_PERCENT must be a whole number from 0 to 90 (got '${BACKUP_MIN_FREE_PERCENT}')." >&2; exit 1; }
[[ $EUID -eq 0 ]] || { echo "[✗] backup.sh must run as root." >&2; exit 1; }
[[ -f "$SECRETS_FILE" ]] || { echo "[✗] ${SECRETS_FILE} not found — is this a Servicedesk host?" >&2; exit 1; }

# Recover DB role from secrets.env so we can run pg_dump as that role.
conn="$(grep '^SERVICEDESK_ConnectionStrings__Postgres=' "$SECRETS_FILE" | cut -d= -f2-)"
DB_USER="$(echo "$conn" | grep -oE 'Username=[^;]+' | cut -d= -f2)"
[[ -n "$DB_USER" ]] || { echo "[✗] Could not parse Username from secrets.env." >&2; exit 1; }

gb() { awk -v b="$1" 'BEGIN { printf "%.1f GB", b / 1073741824 }'; }

mkdir -p "${BACKUP_ROOT}"
chmod 700 "${BACKUP_ROOT}"

# Newest complete backup (manifest written last) with a blob copy: the
# hardlink base for unchanged blobs, and the size reference for the dump.
prev_dir=""
for cand in "${BACKUP_ROOT}"/${TS_GLOB}; do
    [[ -d "$cand" && -f "${cand}/manifest.txt" && -d "${cand}/blobs" ]] || continue
    prev_dir="$cand"   # glob expands sorted, so the last match is the newest
done

# ---- Free-space check ---------------------------------------------------
# Dump: the previous dump + 25 % (the database grows slowly), otherwise the
# database's on-disk size — compressed dumps are far smaller, so that is a
# safe upper bound for a first backup.
if [[ -n "$prev_dir" && -f "${prev_dir}/servicedesk.dump" ]]; then
    dump_need=$(( $(stat -c %s "${prev_dir}/servicedesk.dump") * 5 / 4 ))
else
    dump_need="$(sudo -u postgres psql -d postgres -tAc "SELECT pg_database_size('${PG_APP_DB}')" | tr -d '[:space:]')"
fi
# Blobs: exactly what rsync would copy (a dry run against the hardlink base —
# linked files cost no space), or the whole store for a first backup.
blob_need=0
if [[ -d "$BLOB_ROOT" ]]; then
    if [[ -n "$prev_dir" ]]; then
        # A failing dry run leaves blob_need empty → the check below reports it.
        blob_need="$( { LC_ALL=C rsync -a --dry-run --stats --no-human-readable --exclude=/.tmp/ \
            --link-dest="${prev_dir}/blobs" "${BLOB_ROOT}/" "${BACKUP_ROOT}/.space-check/" || true; } \
            | sed -n 's/^Total transferred file size: \([0-9]*\) bytes.*/\1/p')"
    else
        blob_need="$(du -sb --exclude=.tmp "${BLOB_ROOT}" | awk '{print $1}')"
    fi
fi
[[ "$dump_need" =~ ^[0-9]+$ && "$blob_need" =~ ^[0-9]+$ ]] \
    || { echo "[✗] Could not estimate the backup size (dump '${dump_need}', blobs '${blob_need}') — nothing written." >&2; exit 1; }
need=$(( dump_need + blob_need ))
disk_avail="$(df -B1 --output=avail "${BACKUP_ROOT}" | tail -1 | tr -d ' ')"
disk_size="$(df -B1 --output=size "${BACKUP_ROOT}" | tail -1 | tr -d ' ')"
reserve=$(( disk_size * BACKUP_MIN_FREE_PERCENT / 100 ))
if (( disk_avail - need < reserve )); then
    echo "[✗] Not enough disk space for a backup on ${BACKUP_ROOT}: it needs ~$(gb "$need"), $(gb "$disk_avail") is free," >&2
    echo "    and ${BACKUP_MIN_FREE_PERCENT}% of the disk ($(gb "$reserve")) must stay free. Nothing written." >&2
    echo "    Free space, lower BACKUP_RETAIN, or set BACKUP_ROOT to another disk." >&2
    exit 1
fi
echo "[✓] Disk space: backup needs ~$(gb "$need"), $(gb "$disk_avail") free (keeps ${BACKUP_MIN_FREE_PERCENT}% = $(gb "$reserve") free)."

ts="$(date -u +"%Y-%m-%dT%H-%M-%SZ")"
dest="${BACKUP_ROOT}/${ts}"
echo "[i] Backup destination: ${dest}"

# A run that fails halfway (disk full, rsync error, Ctrl+C) must not leave an
# incomplete directory taking up space. Complete = manifest written.
complete=0
remove_incomplete() {
    if (( ! complete )) && [[ -d "$dest" ]]; then
        echo "[!] Backup did not complete — removing ${dest}" >&2
        rm -rf -- "$dest"
    fi
}
trap remove_incomplete EXIT

mkdir -p "${dest}/blobs"
chmod 700 "${dest}"

# ---- Postgres dump ------------------------------------------------------
echo "[i] Dumping Postgres database '${PG_APP_DB}' as role ${DB_USER} …"
# v0.1.24 — performance-monitoring tables (perf_*) are dumped structure-only:
# their data is re-collected within minutes and would only bloat every backup.
# The dump goes to stdout and root writes the file: ${dest} is root-only
# (700), so pg_dump running as postgres cannot open a file in it itself.
sudo -u postgres pg_dump -Fc -d "${PG_APP_DB}" --exclude-table-data='perf_*' > "${dest}/servicedesk.dump"
pg_size="$(du -h "${dest}/servicedesk.dump" | awk '{print $1}')"
echo "[✓] Postgres dump: ${pg_size}"

# ---- Blob rsync ---------------------------------------------------------
if [[ -d "$BLOB_ROOT" ]]; then
    echo "[i] Syncing blob store from ${BLOB_ROOT} …"
    # Ordering matters: the dump above was taken FIRST, so every blob a dumped
    # row references already existed on disk when the dump started and is
    # captured here. Reversing the order could leave rows pointing at blobs
    # that are missing from the backup.
    #
    # Incremental: blobs are immutable and content-addressed, so files that
    # already exist in the previous complete backup are hardlinked instead of
    # copied. Every backup dir stays a self-contained full tree.
    # --delete is intentionally OMITTED so a partial sync never drops files
    # from the backup. Orphan files in the backup are a non-issue.
    # .tmp/ holds in-flight writes and is never part of a backup.
    rsync_args=(-a --exclude=/.tmp/)
    if [[ -n "$prev_dir" ]]; then
        echo "[i] Hardlinking unchanged blobs against ${prev_dir}"
        rsync_args+=("--link-dest=${prev_dir}/blobs")
    else
        echo "[i] No previous backup with blobs found — full copy."
    fi
    rsync "${rsync_args[@]}" "${BLOB_ROOT}/" "${dest}/blobs/"
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

complete=1
echo "[✓] Manifest written."
echo "[✓] Backup complete → ${dest}"

# ---- Rotation -----------------------------------------------------------
# Runs only after everything above succeeded (set -e aborts earlier otherwise).
# Only directories matching this script's own timestamp naming are considered.
if (( BACKUP_RETAIN > 0 )); then
    backups=()
    for d in "${BACKUP_ROOT}"/${TS_GLOB}; do
        [[ -d "$d" && ! -L "$d" ]] && backups+=("$d")
    done
    excess=$(( ${#backups[@]} - BACKUP_RETAIN ))
    if (( excess > 0 )); then
        for ((i=0; i<excess; i++)); do
            victim="${backups[i]}"
            [[ "$victim" != "$dest" ]] || continue
            echo "[i] Pruning old backup: ${victim}"
            rm -rf -- "$victim"
        done
    fi
else
    echo "[i] BACKUP_RETAIN=0 — rotation disabled."
fi
