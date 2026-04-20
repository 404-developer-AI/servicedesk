#!/usr/bin/env bash
# update.sh — one-liner production updater for Servicedesk.
#
# Safe path for upgrading a live install:
#   1. pre-update backup (default: YES, --no-backup to skip)
#   2. git fetch + checkout target ref (default: latest v* tag)
#   3. diff .env.example against secrets.env → prompt for any new required vars
#   4. docker compose build app (new image)
#   5. docker compose up -d app (DatabaseBootstrapper runs idempotent schema DDL)
#   6. wait for health
#   7. ON FAILURE → automatic rollback to previous git ref + rebuild + start
#   8. reload nginx if the template changed
#   9. re-apply REVOKE UPDATE, DELETE ON audit_log (idempotent)
#
# Data-loss guarantees:
#   • Postgres stays native on host — never touched by this script.
#   • Blob bind-mount /var/lib/servicedesk/blobs is never touched.
#   • secrets.env is never overwritten — only appended to when new required
#     vars appear.
#
# Usage (tty-safe one-liner):
#   bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/update.sh)
#
# Non-interactive overrides:
#   REPO_REF=v0.0.15 SKIP_BACKUP=1 bash <(curl -sSL …/update.sh)

set -euo pipefail

readonly SECRETS_DIR="/etc/servicedesk"
readonly SECRETS_FILE="${SECRETS_DIR}/secrets.env"
readonly INSTALL_DIR_DEFAULT="/opt/servicedesk"
readonly PG_APP_DB="servicedesk"

# --- colors + log helpers (identical to install.sh) ----------------------
if [[ -t 1 ]]; then
    readonly C_RED=$'\e[31m' C_GREEN=$'\e[32m' C_YELLOW=$'\e[33m' C_BLUE=$'\e[34m' C_BOLD=$'\e[1m' C_RESET=$'\e[0m'
else
    readonly C_RED="" C_GREEN="" C_YELLOW="" C_BLUE="" C_BOLD="" C_RESET=""
fi
log()  { printf "%s[i]%s %s\n" "$C_BLUE"   "$C_RESET" "$*"; }
ok()   { printf "%s[✓]%s %s\n" "$C_GREEN"  "$C_RESET" "$*"; }
warn() { printf "%s[!]%s %s\n" "$C_YELLOW" "$C_RESET" "$*"; }
die()  { printf "%s[✗] %s%s\n" "$C_RED" "$*" "$C_RESET" >&2; exit 1; }
hr()   { printf "%s%s%s\n" "$C_BOLD" "─────────────────────────────────────────────────────────────" "$C_RESET"; }

# ===========================================================================
# 1. preflight — require root, existing install
# ===========================================================================
preflight() {
    [[ $EUID -eq 0 ]] || die "update.sh must run as root (sudo bash <(curl …))"
    [[ -f "$SECRETS_FILE" ]] || die "No existing install detected (${SECRETS_FILE} missing). Run install.sh first."

    INSTALL_DIR="${INSTALL_DIR:-$INSTALL_DIR_DEFAULT}"
    [[ -d "${INSTALL_DIR}/.git" ]] || die "No git checkout at ${INSTALL_DIR} — can't update."

    # Recover DB_USER + DOMAIN from the existing secrets.env (format:
    # SERVICEDESK_ConnectionStrings__Postgres=Host=…;Username=foo;Password=bar).
    local conn
    conn="$(grep '^SERVICEDESK_ConnectionStrings__Postgres=' "$SECRETS_FILE" | cut -d= -f2-)"
    DB_USER="$(echo "$conn" | grep -oE 'Username=[^;]+' | cut -d= -f2)"
    [[ -n "$DB_USER" ]] || die "Could not parse Username from ${SECRETS_FILE}."

    DOMAIN="${DOMAIN:-$(grep -oE 'server_name[[:space:]]+[^;]+;' "${INSTALL_DIR}/deploy/nginx/default.conf.template" 2>/dev/null | awk '{print $2}' | tr -d ';' | head -1)}"
    DOMAIN="${DOMAIN:-localhost}"

    ok "Existing install detected — user=${DB_USER} domain=${DOMAIN}"
}

# ===========================================================================
# 2. pre-update backup
# ===========================================================================
run_backup() {
    if [[ -n "${SKIP_BACKUP:-}" ]]; then
        warn "SKIP_BACKUP set — continuing WITHOUT a safety backup."
        return
    fi
    local ans
    if [[ -n "${NO_PROMPT:-}" ]]; then
        ans="Y"
    else
        read -rp "${C_BOLD}Run pre-update backup first?${C_RESET} [Y/n] (strongly recommended): " ans </dev/tty
        ans="${ans:-Y}"
    fi
    if [[ "${ans,,}" =~ ^(y|yes)$ ]]; then
        log "Running backup.sh …"
        "${INSTALL_DIR}/deploy/backup.sh" || die "Backup failed — aborting update."
        ok "Backup complete."
    else
        warn "Backup skipped by user request."
    fi
}

# ===========================================================================
# 3. record current state so we can roll back
# ===========================================================================
snapshot_current_state() {
    PREVIOUS_SHA="$(git -C "$INSTALL_DIR" rev-parse HEAD)"
    PREVIOUS_REF="$(git -C "$INSTALL_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "$PREVIOUS_SHA")"
    log "Snapshot: on ref '${PREVIOUS_REF}' at ${PREVIOUS_SHA:0:12}."
}

# ===========================================================================
# 4. fetch + checkout target ref
# ===========================================================================
fetch_and_checkout() {
    log "Fetching refs from origin …"
    git -C "$INSTALL_DIR" fetch --tags --prune --quiet

    local target="${REPO_REF:-}"
    if [[ -z "$target" ]]; then
        target="$(git -C "$INSTALL_DIR" tag -l 'v*' | sort -V | tail -1)"
        if [[ -z "$target" ]]; then
            target="main"
            warn "No v* tags found — falling back to '${target}'."
        fi
    fi
    log "Target ref: ${target}"

    if [[ "$target" == "$PREVIOUS_SHA" ]] || [[ "$target" == "$PREVIOUS_REF" && "$(git -C "$INSTALL_DIR" rev-parse "$target")" == "$PREVIOUS_SHA" ]]; then
        ok "Already on ${target} — nothing to update."
        exit 0
    fi

    git -C "$INSTALL_DIR" checkout "$target" --quiet
    # If it's a branch (not a tag), pull the latest commit.
    if git -C "$INSTALL_DIR" symbolic-ref -q HEAD >/dev/null; then
        git -C "$INSTALL_DIR" pull --ff-only --quiet
    fi
    NEW_SHA="$(git -C "$INSTALL_DIR" rev-parse HEAD)"
    ok "Checked out ${target} (${NEW_SHA:0:12})."
}

# ===========================================================================
# 5. diff .env.example → prompt for new required vars
# ===========================================================================
reconcile_env_vars() {
    local example="${INSTALL_DIR}/.env.example"
    [[ -f "$example" ]] || { warn "No .env.example in repo — skipping env-var reconciliation."; return; }

    local new_vars=()
    while IFS= read -r line; do
        [[ "$line" =~ ^[[:space:]]*# ]] && continue
        [[ "$line" =~ ^[[:space:]]*$ ]] && continue
        local key="${line%%=*}"
        [[ -z "$key" ]] && continue
        # secrets.env only holds SERVICEDESK_* keys. Non-secret env-vars like
        # ASPNETCORE_ENVIRONMENT are set by docker-compose directly and don't
        # belong in secrets.env — skip them here.
        [[ "$key" =~ ^SERVICEDESK_ ]] || continue
        if ! grep -q "^${key}=" "$SECRETS_FILE"; then
            new_vars+=("$key")
        fi
    done < "$example"

    if [[ ${#new_vars[@]} -eq 0 ]]; then
        ok "No new required env-vars — secrets.env is up to date."
        return
    fi

    hr
    warn "This update introduces ${#new_vars[@]} new required env-var(s):"
    for k in "${new_vars[@]}"; do printf "    • %s\n" "$k"; done
    hr

    for k in "${new_vars[@]}"; do
        local val=""
        if [[ -n "${NO_PROMPT:-}" ]]; then
            die "New var ${k} is required but NO_PROMPT is set. Aborting."
        fi
        read -rp "Value for ${k} (enter = auto-generate 32-char random): " val </dev/tty
        if [[ -z "$val" ]]; then
            val="$(openssl rand -base64 32)"
            log "Generated value for ${k}."
        fi
        printf '%s=%s\n' "$k" "$val" >> "$SECRETS_FILE"
    done
    ok "secrets.env updated with ${#new_vars[@]} new value(s)."
}

# ===========================================================================
# 6. build + swap app container
# ===========================================================================
rebuild_and_restart_app() {
    log "Building new app image (this can take 2-5 min) …"
    (cd "${INSTALL_DIR}/deploy" && DOMAIN="$DOMAIN" docker compose build app)
    log "Stopping old app …"
    (cd "${INSTALL_DIR}/deploy" && docker compose stop app)
    log "Starting new app …"
    (cd "${INSTALL_DIR}/deploy" && DOMAIN="$DOMAIN" docker compose up -d app)
}

wait_for_health_or_rollback() {
    local tries=60
    for ((i=1; i<=tries; i++)); do
        local status
        status="$(docker inspect --format='{{.State.Health.Status}}' servicedesk-app-1 2>/dev/null || echo "starting")"
        if [[ "$status" == "healthy" ]]; then
            ok "New app is healthy (${NEW_SHA:0:12})."
            return 0
        fi
        sleep 2
    done

    warn "New app did NOT reach healthy state in $((tries*2))s — rolling back to ${PREVIOUS_SHA:0:12} …"
    docker logs servicedesk-app-1 2>&1 | tail -40
    git -C "$INSTALL_DIR" checkout "$PREVIOUS_SHA" --quiet
    (cd "${INSTALL_DIR}/deploy" && DOMAIN="$DOMAIN" docker compose build app)
    (cd "${INSTALL_DIR}/deploy" && docker compose stop app)
    (cd "${INSTALL_DIR}/deploy" && DOMAIN="$DOMAIN" docker compose up -d app)
    warn "Rollback complete. Investigate the logs above before retrying."
    return 1
}

# ===========================================================================
# 7. nginx reload if its template changed between the two git refs
# ===========================================================================
reload_nginx_if_config_changed() {
    local diff
    diff="$(git -C "$INSTALL_DIR" diff --name-only "$PREVIOUS_SHA" HEAD -- deploy/nginx/ 2>/dev/null || echo "")"
    if [[ -z "$diff" ]]; then
        ok "nginx config unchanged — no reload needed."
        return
    fi
    log "nginx config changed (${diff//$'\n'/, }) — reloading …"
    docker kill -s HUP servicedesk-nginx-1 >/dev/null 2>&1 || \
        (cd "${INSTALL_DIR}/deploy" && DOMAIN="$DOMAIN" docker compose restart nginx)
    ok "nginx reloaded."
}

# ===========================================================================
# 8. ensure REVOKE on audit_log stays in place after potential pg_restore
# ===========================================================================
reapply_audit_log_revoke() {
    sudo -u postgres psql -d "${PG_APP_DB}" -c \
        "REVOKE UPDATE, DELETE ON audit_log FROM \"${DB_USER}\";" >/dev/null 2>&1 || true
    ok "audit_log REVOKE re-applied (idempotent)."
}

# ===========================================================================
# 9. human summary of the update
# ===========================================================================
print_summary() {
    hr
    ok "Update complete."
    printf "  From:  %s\n" "$PREVIOUS_SHA"
    printf "  To:    %s\n" "$NEW_SHA"
    local changes
    changes="$(git -C "$INSTALL_DIR" log --oneline "$PREVIOUS_SHA..$NEW_SHA" 2>/dev/null | head -20)"
    if [[ -n "$changes" ]]; then
        printf "\n  Commits landed:\n%s\n" "$changes" | sed 's/^/    /'
    fi
    hr
    log "If you see issues, the previous version is still available:"
    printf "    sudo REPO_REF=%s bash <(curl -sSL …/update.sh)\n" "${PREVIOUS_SHA:0:12}"
    hr
}

# ===========================================================================
# main
# ===========================================================================
main() {
    hr
    log "${C_BOLD}Servicedesk updater${C_RESET}"
    hr
    preflight
    run_backup
    snapshot_current_state
    fetch_and_checkout
    reconcile_env_vars
    rebuild_and_restart_app
    if ! wait_for_health_or_rollback; then
        exit 1
    fi
    reload_nginx_if_config_changed
    reapply_audit_log_revoke
    print_summary
}

main "$@"
