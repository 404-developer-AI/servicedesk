#!/usr/bin/env bash
# servicedesk-cert-renew.sh — host-side Let's Encrypt renewal helper.
#
# Triggered by /etc/systemd/system/servicedesk-cert-renew.path when the
# servicedesk app container drops /var/lib/servicedesk/cert-renew/renew.request.
# Runs certbot in the existing compose `certs`-profile container, HUPs nginx
# on success, writes the outcome back to renew.status so the app health card
# can render the "Last renew attempt" detail line.
#
# Security note: this script is the ONLY component on the host that has
# docker / certbot privileges. The servicedesk app container NEVER mounts the
# docker socket — the signal-file bridge keeps the container unprivileged.

set -euo pipefail

readonly SIGNAL_DIR="/var/lib/servicedesk/cert-renew"
readonly REQUEST_FILE="${SIGNAL_DIR}/renew.request"
readonly STATUS_FILE="${SIGNAL_DIR}/renew.status"
readonly LOCK_FILE="/var/lock/servicedesk-cert-renew.lock"
readonly APP_UID="10001"
readonly APP_GID="10001"
readonly NGINX_CONTAINER="servicedesk-nginx-1"
readonly ENV_CONF="/etc/servicedesk/env.conf"

# Load env.conf — provides SERVICEDESK_TlsCert__Domain, APP_LE_EMAIL,
# APP_INSTALL_DIR. Set -a / +a exports so any child processes inherit.
if [[ -f "$ENV_CONF" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$ENV_CONF"
    set +a
fi

DOMAIN="${SERVICEDESK_TlsCert__Domain:-}"
EMAIL="${APP_LE_EMAIL:-}"
INSTALL_DIR="${APP_INSTALL_DIR:-/opt/servicedesk}"

write_status() {
    local state=$1
    local detail=${2:-}
    local utc
    utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    local tmp
    tmp="$(mktemp "${STATUS_FILE}.XXXXXX")"
    {
        printf 'state=%s\n' "$state"
        printf 'utc=%s\n' "$utc"
        [[ -n "$detail" ]] && printf 'detail=%s\n' "$detail"
    } > "$tmp"
    mv "$tmp" "$STATUS_FILE"
    # The app container (uid 10001) must be able to read; systemd writes as
    # root. 644 + matching owner keeps it readable across the bind mount.
    chown "$APP_UID:$APP_GID" "$STATUS_FILE" 2>/dev/null || true
    chmod 644 "$STATUS_FILE"
}

# Single-flight guard — rapid clicks should fold into one certbot run, not
# a queue. Non-blocking flock: a later invocation exits 0 without touching
# the status so the in-progress run's state stays authoritative.
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
    rm -f "$REQUEST_FILE"
    exit 0
fi

# Clear the request file immediately so the next click re-arms the
# IN_CLOSE_WRITE trigger on the path unit. Lock + status together serialize
# concurrent clicks into one run.
rm -f "$REQUEST_FILE"

if [[ -z "$DOMAIN" ]]; then
    write_status "failed" "SERVICEDESK_TlsCert__Domain not set in ${ENV_CONF}"
    exit 1
fi
if [[ -z "$EMAIL" ]]; then
    write_status "failed" "APP_LE_EMAIL not set in ${ENV_CONF}"
    exit 1
fi
if [[ ! -d "$INSTALL_DIR/deploy" ]]; then
    write_status "failed" "APP_INSTALL_DIR=${INSTALL_DIR} does not contain deploy/"
    exit 1
fi

write_status "running" "certbot starting…"

# Webroot challenge — nginx continues to serve on :80 and :443 throughout
# the renewal; Let's Encrypt hits /.well-known/acme-challenge/<token> on :80
# which nginx routes to the acme-webroot volume the certbot container also
# mounts. `docker compose run --rm` is one-shot; no long-running sidecar.
log="$(mktemp)"
if (cd "$INSTALL_DIR/deploy" && \
        DOMAIN="$DOMAIN" docker compose run --rm --entrypoint certbot certbot \
            certonly --webroot -w /var/www/certbot \
            -d "$DOMAIN" \
            --email "$EMAIL" \
            --agree-tos --non-interactive --no-eff-email) > "$log" 2>&1; then
    # HUP makes nginx re-read its config + ssl_certificate / ssl_certificate_key
    # files without dropping active connections. Old workers finish in-flight
    # requests; new workers pick up the fresh cert.
    if docker kill -s HUP "$NGINX_CONTAINER" >/dev/null 2>&1; then
        write_status "success" "Renewed; nginx reloaded."
    else
        write_status "success" "Renewed; nginx HUP failed — run: docker compose restart nginx"
    fi
    rm -f "$log"
    exit 0
else
    # Trim — the detail field lands in a HealthDetail value and a thousand
    # lines of certbot output would wreck the card's layout. Full output
    # stays in `journalctl -u servicedesk-cert-renew`.
    tail_log="$(tail -c 400 "$log" | tr '\n' ' ')"
    write_status "failed" "certbot exit non-zero: ${tail_log:-(see journalctl -u servicedesk-cert-renew)}"
    rm -f "$log"
    exit 1
fi
