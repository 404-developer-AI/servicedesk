using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Ingest;

/// Reads <see cref="SettingKeys.Mail.AutoLinkDomainBlacklist"/> (a JSON array
/// of freemail/public domains) into a case-insensitive set. Consumed both by
/// <see cref="ContactLookupService"/> — to skip auto-linking when a sender's
/// domain is on the list — and by the admin Companies→Domains endpoint, which
/// refuses to store any blacklisted value as a company-owned domain.
///
/// Failures (missing key, malformed JSON) degrade to an empty set so the mail
/// intake path never breaks on a misconfigured setting.
public static class MailDomainBlacklist
{
    public static async Task<HashSet<string>> LoadAsync(
        ISettingsService settings, ILogger? logger, CancellationToken ct)
    {
        try
        {
            var json = await settings.GetAsync<string>(SettingKeys.Mail.AutoLinkDomainBlacklist, ct);
            if (string.IsNullOrWhiteSpace(json))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var arr = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in arr)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                set.Add(d.Trim().ToLowerInvariant());
            }
            return set;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read Mail.AutoLinkDomainBlacklist; treating as empty.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
