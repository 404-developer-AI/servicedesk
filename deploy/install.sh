#!/usr/bin/env bash
# install.sh — one-link production installer for Servicedesk.
#
# Usage (tty-safe one-liner — process-substitution keeps stdin on the TTY so
# `read` works during prompts):
#
#     bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/install.sh)
#
# Non-interactive overrides (all optional — prompts fill in the rest):
#
#     DOMAIN=sd.example.com \
#     EMAIL=ops@example.com \
#     SSL=yes \
#     DB_USER=servicedesk_app \
#     DB_PASSWORD=<strong-password-or-empty-to-generate> \
#     INSTALL_DIR=/opt/servicedesk \
#     REPO_URL=https://github.com/404-developer-AI/servicedesk.git \
#     REPO_REF=main \
#     bash <(curl -sSL https://raw.githubusercontent.com/404-developer-AI/servicedesk/main/deploy/install.sh)
#
# Idempotent: re-running on a healthy install is safe — every step checks
# state before acting. Secrets are only generated on first run; an existing
# /etc/servicedesk/secrets.env is never overwritten.

set -euo pipefail

# ===========================================================================
# Globals + helpers
# ===========================================================================
readonly SECRETS_DIR="/etc/servicedesk"
readonly SECRETS_FILE="${SECRETS_DIR}/secrets.env"
readonly BLOB_ROOT="/var/lib/servicedesk/blobs"
readonly SUMMARY_FILE="/root/servicedesk-install-summary.txt"
readonly INSTALL_DIR_DEFAULT="/opt/servicedesk"
readonly REPO_URL_DEFAULT="https://github.com/404-developer-AI/servicedesk.git"
readonly REPO_REF_DEFAULT="main"
readonly PG_APP_DB="servicedesk"
readonly PG_VERSION_TARGET="16"

# Color output — auto-disabled when stdout is not a TTY.
if [[ -t 1 ]]; then
    readonly C_RED=$'\e[31m'
    readonly C_GREEN=$'\e[32m'
    readonly C_YELLOW=$'\e[33m'
    readonly C_BLUE=$'\e[34m'
    readonly C_BOLD=$'\e[1m'
    readonly C_RESET=$'\e[0m'
else
    readonly C_RED="" C_GREEN="" C_YELLOW="" C_BLUE="" C_BOLD="" C_RESET=""
fi

log()   { printf "%s[i]%s %s\n" "$C_BLUE" "$C_RESET" "$*"; }
ok()    { printf "%s[✓]%s %s\n" "$C_GREEN" "$C_RESET" "$*"; }
warn()  { printf "%s[!]%s %s\n" "$C_YELLOW" "$C_RESET" "$*"; }
die()   { printf "%s[✗] %s%s\n" "$C_RED" "$*" "$C_RESET" >&2; exit 1; }
hr()    { printf "%s%s%s\n" "$C_BOLD" "─────────────────────────────────────────────────────────────" "$C_RESET"; }

# ===========================================================================
# prompt helpers
# ===========================================================================
# prompt_default VAR "question" "default"
prompt_default() {
    local __var=$1; local __q=$2; local __def=$3
    if [[ -n "${!__var:-}" ]]; then
        log "Using ${__var} from environment: ${!__var}"
        return
    fi
    local __ans
    read -rp "${C_BOLD}${__q}${C_RESET} [${__def}]: " __ans </dev/tty
    printf -v "$__var" '%s' "${__ans:-$__def}"
}

# prompt_yesno VAR "question" "Y|N"
prompt_yesno() {
    local __var=$1; local __q=$2; local __def=$3
    if [[ -n "${!__var:-}" ]]; then
        log "Using ${__var} from environment: ${!__var}"
        return
    fi
    local __hint; [[ "$__def" == "Y" ]] && __hint="Y/n" || __hint="y/N"
    local __ans
    read -rp "${C_BOLD}${__q}${C_RESET} [${__hint}]: " __ans </dev/tty
    __ans=${__ans:-$__def}
    case "${__ans,,}" in
        y|yes) printf -v "$__var" 'yes' ;;
        n|no)  printf -v "$__var" 'no'  ;;
        *) die "Invalid answer '${__ans}' — expected y or n." ;;
    esac
}

# prompt_password VAR "question" — hidden input + confirm if not env-provided.
prompt_password() {
    local __var=$1; local __q=$2
    if [[ -n "${!__var:-}" ]]; then
        log "Using ${__var} from environment (hidden)."
        return
    fi
    local __p1 __p2
    while true; do
        read -rsp "${C_BOLD}${__q}${C_RESET} (enter = auto-generate): " __p1 </dev/tty; echo
        if [[ -z "$__p1" ]]; then
            __p1="$(openssl rand -base64 32 | tr -d '\n/+=' | head -c 32)"
            log "Generated strong password (32 chars)."
            printf -v "$__var" '%s' "$__p1"
            return
        fi
        read -rsp "Confirm: " __p2 </dev/tty; echo
        if [[ "$__p1" == "$__p2" ]]; then
            printf -v "$__var" '%s' "$__p1"
            return
        fi
        warn "Passwords do not match — try again."
    done
}

# ===========================================================================
# 1. check_os + root
# ===========================================================================
check_os_and_root() {
    [[ $EUID -eq 0 ]] || die "install.sh must run as root (try: sudo bash <(curl …))"

    [[ -f /etc/os-release ]] || die "/etc/os-release not found — only Ubuntu LTS is supported."
    . /etc/os-release
    case "${ID:-}:${VERSION_ID:-}" in
        ubuntu:22.04|ubuntu:24.04) ok "Ubuntu ${VERSION_ID} detected." ;;
        *) die "Unsupported OS: ${PRETTY_NAME:-unknown}. Require Ubuntu 22.04 or 24.04." ;;
    esac
}

# ===========================================================================
# 2. prompts
# ===========================================================================
collect_prompts() {
    hr
    log "${C_BOLD}Servicedesk installer — gathering configuration${C_RESET}"
    hr

    prompt_yesno SSL "Enable TLS via Let's Encrypt?" "Y"
    if [[ "$SSL" == "yes" ]]; then
        prompt_default DOMAIN "Public domain (A-record must already point to this host)" "servicedesk.example.com"
        prompt_default EMAIL  "Email for Let's Encrypt expiry notices" "admin@${DOMAIN}"
    else
        prompt_default DOMAIN "Public hostname or IP (for reverse proxy)" "$(hostname -f 2>/dev/null || echo "localhost")"
        EMAIL="${EMAIL:-disabled@example.com}"
    fi

    prompt_default DB_USER "Postgres app role" "servicedesk_app"
    prompt_password DB_PASSWORD "App-database password"

    INSTALL_DIR="${INSTALL_DIR:-$INSTALL_DIR_DEFAULT}"
    REPO_URL="${REPO_URL:-$REPO_URL_DEFAULT}"
    REPO_REF="${REPO_REF:-$REPO_REF_DEFAULT}"

    hr
    log "Configuration summary:"
    printf "  Domain:       %s\n" "$DOMAIN"
    printf "  SSL:          %s\n" "$SSL"
    [[ "$SSL" == "yes" ]] && printf "  Email:        %s\n" "$EMAIL"
    printf "  DB user:      %s\n" "$DB_USER"
    printf "  DB password:  %s\n" "********"
    printf "  Install dir:  %s\n" "$INSTALL_DIR"
    printf "  Repo:         %s @ %s\n" "$REPO_URL" "$REPO_REF"
    hr

    local confirm="${CONFIRM:-}"
    if [[ -z "$confirm" ]]; then
        read -rp "${C_BOLD}Proceed with install?${C_RESET} [Y/n]: " confirm </dev/tty
        confirm="${confirm:-Y}"
    fi
    [[ "${confirm,,}" == "y" || "${confirm,,}" == "yes" ]] || die "Aborted by user."
}

# ===========================================================================
# 3. install docker
# ===========================================================================
install_docker() {
    if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
        ok "Docker + compose already present — skipping install."
        return
    fi
    log "Installing Docker + compose plugin from get.docker.com …"
    curl -fsSL https://get.docker.com | sh
    systemctl enable --now docker
    ok "Docker installed."
}

# ===========================================================================
# 4. install postgres (native on host)
# ===========================================================================
install_postgres() {
    if command -v psql >/dev/null 2>&1 && systemctl is-active --quiet postgresql; then
        ok "PostgreSQL already installed and running."
        return
    fi
    log "Installing PostgreSQL ${PG_VERSION_TARGET} …"
    apt-get update -qq
    apt-get install -y -qq "postgresql-${PG_VERSION_TARGET}" postgresql-client
    systemctl enable --now postgresql
    ok "PostgreSQL installed."
}

# ===========================================================================
# 5. configure listen_addresses + pg_hba for the docker bridge
# ===========================================================================
configure_postgres_listen() {
    local pg_conf pg_hba
    pg_conf=$(sudo -u postgres psql -tAc "SHOW config_file")
    pg_hba=$(sudo -u postgres psql -tAc "SHOW hba_file")
    local sentinel="# servicedesk-managed"

    if ! grep -q "$sentinel" "$pg_conf"; then
        log "Configuring postgresql.conf for docker-gateway access …"
        cat >> "$pg_conf" <<EOF

${sentinel} — added by install.sh
listen_addresses = 'localhost,172.17.0.1'
EOF
    fi

    if ! grep -q "$sentinel" "$pg_hba"; then
        log "Configuring pg_hba.conf for docker bridge (172.17.0.0/16) …"
        cat >> "$pg_hba" <<EOF

${sentinel} — added by install.sh
host    ${PG_APP_DB}   ${DB_USER}   172.17.0.0/16   scram-sha-256
EOF
    fi

    systemctl reload postgresql
    ok "Postgres listen + hba configured."
}

# ===========================================================================
# 6. create role + database (idempotent, separate psql-calls — CREATE DATABASE
#    cannot run in a transaction block)
# ===========================================================================
setup_postgres_role_and_db() {
    local exists
    exists=$(sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='${DB_USER}'")
    if [[ "$exists" != "1" ]]; then
        log "Creating Postgres role ${DB_USER} …"
        sudo -u postgres psql -c "CREATE ROLE \"${DB_USER}\" WITH LOGIN PASSWORD '${DB_PASSWORD}';"
    else
        log "Role ${DB_USER} already exists — resetting password to match prompt value."
        sudo -u postgres psql -c "ALTER ROLE \"${DB_USER}\" WITH LOGIN PASSWORD '${DB_PASSWORD}';"
    fi

    exists=$(sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='${PG_APP_DB}'")
    if [[ "$exists" != "1" ]]; then
        log "Creating database ${PG_APP_DB} owned by ${DB_USER} …"
        sudo -u postgres psql -c "CREATE DATABASE ${PG_APP_DB} OWNER \"${DB_USER}\";"
    else
        log "Database ${PG_APP_DB} already exists — leaving untouched."
    fi

    log "Locking down schema public …"
    sudo -u postgres psql -d "${PG_APP_DB}" -c "REVOKE ALL ON SCHEMA public FROM PUBLIC;" >/dev/null
    sudo -u postgres psql -d "${PG_APP_DB}" -c "GRANT USAGE, CREATE ON SCHEMA public TO \"${DB_USER}\";" >/dev/null

    ok "Postgres role + database ready."
}

# ===========================================================================
# 7. generate secrets.env (skip if file already exists — protects reruns)
# ===========================================================================
generate_secrets_env() {
    mkdir -p "$SECRETS_DIR"

    if [[ -f "$SECRETS_FILE" ]]; then
        ok "${SECRETS_FILE} already exists — leaving untouched (delete it first if you want to regenerate)."
        return
    fi

    log "Generating ${SECRETS_FILE} …"
    local hash_key master_key
    hash_key="$(openssl rand -base64 32)"
    master_key="$(openssl rand -base64 32)"

    local tmp
    tmp="$(mktemp)"
    cat > "$tmp" <<EOF
# Servicedesk production secrets — managed by install.sh
# Generated: $(date -u +"%Y-%m-%dT%H:%M:%SZ")
# Mode: 600 root:root. Keep out of git, rsync, or any backup that is not encrypted.

SERVICEDESK_ConnectionStrings__Postgres=Host=host.docker.internal;Port=5432;Database=${PG_APP_DB};Username=${DB_USER};Password=${DB_PASSWORD}
SERVICEDESK_Audit__HashKey=${hash_key}
SERVICEDESK_DataProtection__MasterKey=${master_key}
EOF
    install -m 600 -o root -g root "$tmp" "$SECRETS_FILE"
    rm -f "$tmp"
    ok "secrets.env written (600 root:root)."
}

# ===========================================================================
# 8. blob-store directory
# ===========================================================================
prepare_blob_root() {
    if [[ -d "$BLOB_ROOT" ]]; then
        ok "Blob root ${BLOB_ROOT} already exists."
    else
        log "Creating blob root ${BLOB_ROOT} …"
        install -d -m 755 -o 10001 -g 10001 "$BLOB_ROOT"
    fi
}

# ===========================================================================
# 9. clone or update the repo at INSTALL_DIR
# ===========================================================================
clone_repo() {
    if [[ ! -d "$INSTALL_DIR/.git" ]]; then
        log "Cloning ${REPO_URL} into ${INSTALL_DIR} …"
        mkdir -p "$(dirname "$INSTALL_DIR")"
        git clone --branch "$REPO_REF" "$REPO_URL" "$INSTALL_DIR"
    else
        log "Repo already present at ${INSTALL_DIR} — fetching refs."
        git -C "$INSTALL_DIR" fetch --tags --quiet
        git -C "$INSTALL_DIR" checkout "$REPO_REF" --quiet
        git -C "$INSTALL_DIR" pull --ff-only --quiet || true
    fi
    ok "Repo at ${INSTALL_DIR} on ${REPO_REF}."
}

# ===========================================================================
# 10. nginx template selection based on SSL choice
# ===========================================================================
select_nginx_template() {
    local src
    if [[ "$SSL" == "yes" ]]; then
        src="${INSTALL_DIR}/deploy/nginx/default.conf.template"
    else
        src="${INSTALL_DIR}/deploy/nginx/default-http-only.conf.template"
    fi
    # The compose file bind-mounts deploy/nginx/default.conf.template. For
    # the http-only path, replace that file with the no-TLS variant so the
    # bind-mount picks up the right config without compose edits.
    local target="${INSTALL_DIR}/deploy/nginx/default.conf.template"
    if [[ "$src" != "$target" ]]; then
        cp -f "$src" "$target"
        ok "Selected http-only nginx template."
    else
        ok "Using TLS nginx template."
    fi
}

# ===========================================================================
# 11. build + start app (pre-certbot, nginx not yet up)
# ===========================================================================
start_app() {
    log "Building servicedesk image (first run: ~3-5 min for node + dotnet) …"
    export APP_VERSION
    APP_VERSION="$(cd "$INSTALL_DIR" && git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo "0.0.0-install")"
    (cd "$INSTALL_DIR/deploy" && DOMAIN="$DOMAIN" docker compose build app)
    (cd "$INSTALL_DIR/deploy" && DOMAIN="$DOMAIN" docker compose up -d app)
    ok "App container started — waiting for health …"
}

wait_for_app_health() {
    local tries=60
    for ((i=1; i<=tries; i++)); do
        local status
        status="$(docker inspect --format='{{.State.Health.Status}}' servicedesk-app-1 2>/dev/null || echo "starting")"
        if [[ "$status" == "healthy" ]]; then
            ok "App is healthy."
            return
        fi
        sleep 2
    done
    docker logs servicedesk-app-1 | tail -50
    die "App did not become healthy in $((tries*2))s — see log above."
}

# ===========================================================================
# 12. post-install: REVOKE audit_log mutations (the table exists after the
#     first DatabaseBootstrapper run).
# ===========================================================================
post_install_revoke_audit_log() {
    log "Applying audit_log REVOKE (UPDATE/DELETE — tamper-evidence)…"
    sudo -u postgres psql -d "${PG_APP_DB}" -c \
        "REVOKE UPDATE, DELETE ON audit_log FROM \"${DB_USER}\";" >/dev/null
    ok "audit_log is now append-only for the app role."
}

# ===========================================================================
# 13. first-issue Let's Encrypt cert (only when SSL=yes)
# ===========================================================================
bootstrap_certbot() {
    if [[ "$SSL" != "yes" ]]; then
        log "SSL=no → skipping Let's Encrypt certbot step."
        return
    fi
    # Do we already have a cert?
    if docker run --rm -v servicedesk_certs:/etc/letsencrypt alpine \
           test -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" 2>/dev/null; then
        ok "TLS cert for ${DOMAIN} already present — skipping issue."
        return
    fi
    log "Issuing initial Let's Encrypt cert (standalone, port 80) …"
    log "Temporarily freeing :80 — nginx has not been started yet, so nothing else should hold it."
    (cd "$INSTALL_DIR/deploy" && docker compose run --rm --service-ports --entrypoint certbot certbot \
        certonly --standalone \
        -d "$DOMAIN" \
        --email "$EMAIL" \
        --agree-tos --non-interactive --no-eff-email)
    ok "TLS cert issued for ${DOMAIN}."
}

# ===========================================================================
# 14. start nginx
# ===========================================================================
start_nginx() {
    log "Starting nginx …"
    (cd "$INSTALL_DIR/deploy" && DOMAIN="$DOMAIN" docker compose up -d nginx)
    ok "nginx started."
}

# ===========================================================================
# 15. write human-readable summary + delete-reminder
# ===========================================================================
write_summary() {
    local proto="http"
    [[ "$SSL" == "yes" ]] && proto="https"
    local base_url="${proto}://${DOMAIN}"

    umask 077
    cat > "$SUMMARY_FILE" <<EOF
===========================================================================
Servicedesk install summary — $(date -u +"%Y-%m-%dT%H:%M:%SZ")
===========================================================================

App URL:            ${base_url}
Setup wizard:       ${base_url}/setup
  → Open this URL in a browser to create the first admin account.

Install directory:  ${INSTALL_DIR}
Secrets file:       ${SECRETS_FILE}       (mode 600 root:root)
Summary file:       ${SUMMARY_FILE}       (this file — mode 600 root:root)

Postgres:
  Role:             ${DB_USER}
  Database:         ${PG_APP_DB}
  Password:         ${DB_PASSWORD}
  Connection:       Host=host.docker.internal;Port=5432;Database=${PG_APP_DB};Username=${DB_USER}

TLS:                ${SSL}
$([[ "$SSL" == "yes" ]] && echo "Let's Encrypt cert: /etc/letsencrypt/live/${DOMAIN}/fullchain.pem (inside certbot volume)")

Day-to-day operations:
  Update:           bash <(curl -sSL ${REPO_URL%%.git}/raw/${REPO_REF}/deploy/update.sh)
  Backup:           sudo ${INSTALL_DIR}/deploy/backup.sh
  Restore:          sudo ${INSTALL_DIR}/deploy/restore.sh <backup-dir>
  Container status: docker compose -f ${INSTALL_DIR}/deploy/docker-compose.yml ps
  App logs:         docker logs -f servicedesk-app-1
  nginx logs:       docker logs -f servicedesk-nginx-1

===========================================================================
SECURITY — DELETE THIS FILE ONCE YOU HAVE COPIED THE VALUES TO A PASSWORD
MANAGER:

    shred -u ${SUMMARY_FILE}

This file contains the DB password + URLs in plaintext. It is mode 600 and
owned by root, but there is no reason to keep it on disk after setup.
===========================================================================
EOF

    hr
    ok "Install complete."
    hr
    cat "$SUMMARY_FILE"
    hr
    printf "%s⚠  A copy of the above summary is saved to %s.\n" "$C_YELLOW" "$SUMMARY_FILE"
    printf "   Copy the secrets to a password manager, then DELETE the file:\n"
    printf "     %sshred -u %s%s\n" "$C_BOLD" "$SUMMARY_FILE" "$C_RESET"
    hr
}

# ===========================================================================
# main
# ===========================================================================
main() {
    hr
    log "${C_BOLD}Servicedesk installer${C_RESET}"
    hr

    check_os_and_root
    collect_prompts
    install_docker
    install_postgres
    configure_postgres_listen
    setup_postgres_role_and_db
    generate_secrets_env
    prepare_blob_root
    clone_repo
    select_nginx_template
    start_app
    wait_for_app_health
    post_install_revoke_audit_log
    bootstrap_certbot
    start_nginx
    write_summary
}

main "$@"
