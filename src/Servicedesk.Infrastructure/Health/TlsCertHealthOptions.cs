namespace Servicedesk.Infrastructure.Health;

/// Runtime config for the <c>tls-cert</c> health subsystem. Bound from the
/// <c>TlsCert</c> configuration section, which in production is populated by
/// <c>SERVICEDESK_TlsCert__Domain</c> written to <c>/etc/servicedesk/env.conf</c>
/// by <c>install.sh</c> / <c>update.sh</c>.
public sealed class TlsCertHealthOptions
{
    /// Public domain on the Let's Encrypt certificate. When empty the subsystem
    /// reports "monitoring disabled" — typical of the SSL=no install path or
    /// installs that predate v0.0.18 (update.sh backfills this on upgrade).
    public string? Domain { get; set; }

    /// Read-only mount of the certbot <c>certs</c> volume. Combined with
    /// <see cref="Domain"/> as <c>{CertDirectory}/{Domain}/fullchain.pem</c>.
    public string CertDirectory { get; set; } = "/etc/letsencrypt/live";

    /// Signal directory shared read-write with the host-side
    /// <c>servicedesk-cert-renew.path</c> systemd unit. The renew-now action
    /// drops <c>renew.request</c> here; the helper writes <c>renew.status</c>
    /// back with the outcome of the last run.
    public string SignalDirectory { get; set; } = "/var/lib/servicedesk/cert-renew";

    /// Warning threshold — below this many days until expiry the card turns
    /// amber. Default 14 matches Let's Encrypt's renewal window (they refuse
    /// to renew earlier than 30 days before expiry, so a 14-day warning gives
    /// two weeks of actionable lead-time).
    public int WarningDays { get; set; } = 14;

    /// Critical threshold — below this many days until expiry the card turns
    /// red. Default 7: at one week out nginx is about to serve an expired
    /// cert and every browser will refuse the TLS handshake.
    public int CriticalDays { get; set; } = 7;
}
