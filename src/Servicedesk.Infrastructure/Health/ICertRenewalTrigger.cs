using Microsoft.Extensions.Options;

namespace Servicedesk.Infrastructure.Health;

/// Kicks the host-side cert-renewal helper by dropping a signal file in the
/// shared <see cref="TlsCertHealthOptions.SignalDirectory"/>. A
/// <c>systemd.path</c> unit on the host watches for <c>renew.request</c> and
/// invokes <c>servicedesk-cert-renew.sh</c>, which runs certbot in the
/// existing <c>--profile certs</c> compose container and HUPs nginx to
/// reload the new cert. The app container stays unprivileged — it never
/// touches the docker socket.
public interface ICertRenewalTrigger
{
    Task TriggerAsync(CancellationToken ct);

    /// Reads the host helper's status file — written with the outcome of the
    /// most recent run. <c>null</c> when no run has happened yet.
    CertRenewalStatus? TryReadStatus();
}

public sealed record CertRenewalStatus(string State, DateTime WhenUtc, string? Detail);

public sealed class FileSignalCertRenewalTrigger : ICertRenewalTrigger
{
    private const string RequestFile = "renew.request";
    private const string StatusFile = "renew.status";

    private readonly IOptions<TlsCertHealthOptions> _options;

    public FileSignalCertRenewalTrigger(IOptions<TlsCertHealthOptions> options)
    {
        _options = options;
    }

    public async Task TriggerAsync(CancellationToken ct)
    {
        var dir = _options.Value.SignalDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, RequestFile);
        // UTC timestamp as the payload — not read by the helper, but useful
        // for correlating journal entries with the admin click that
        // triggered the renewal.
        var payload = DateTime.UtcNow.ToString("u") + Environment.NewLine;
        await File.WriteAllTextAsync(path, payload, ct);
    }

    public CertRenewalStatus? TryReadStatus()
    {
        var path = Path.Combine(_options.Value.SignalDirectory, StatusFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // Status file is KEY=VALUE per line, written atomically by the
            // helper via `mv tmp final`. Parse defensively — a partially
            // written file should never crash the health endpoint.
            string? state = null;
            string? whenText = null;
            string? detail = null;
            foreach (var line in File.ReadAllLines(path))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line[..idx];
                var value = line[(idx + 1)..];
                switch (key)
                {
                    case "state": state = value; break;
                    case "utc": whenText = value; break;
                    case "detail": detail = value; break;
                }
            }
            if (state is null || whenText is null) return null;
            if (!DateTime.TryParse(whenText, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var when))
            {
                return null;
            }
            return new CertRenewalStatus(state, when, detail);
        }
        catch
        {
            return null;
        }
    }
}
