using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Ingest;

/// <see cref="SettingKeys.Mail.AutoSubmittedAllowedSenders"/>: senders whose
/// automated mail (auto-submitted / bulk) may open a new ticket even without
/// a thread match — e.g. a supplier's daily service report from a no-reply
/// address. Entries are comma-, semicolon- or whitespace-separated:
/// <c>noreply@example.com</c> matches that exact address, <c>@example.com</c>
/// or <c>example.com</c> matches every address on exactly that domain
/// (subdomains deliberately not included — allow them explicitly).
///
/// The resulting ticket still carries the auto-reply flag, so the hard
/// loop-prevention gate in <c>SendMailHandler</c> keeps suppressing trigger
/// mail back to the sender. Failures degrade to "nothing allowed" so intake
/// never breaks on a misconfigured setting.
public static class AutoSubmittedSenderAllowList
{
    private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };

    public static async Task<bool> IsAllowedAsync(
        ISettingsService settings, string fromAddress, ILogger? logger, CancellationToken ct)
    {
        try
        {
            var raw = await settings.GetAsync<string>(SettingKeys.Mail.AutoSubmittedAllowedSenders, ct);
            return Matches(raw, fromAddress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read Mail.AutoSubmittedAllowedSenders; treating as empty.");
            return false;
        }
    }

    public static bool Matches(string? rawList, string? fromAddress)
    {
        if (string.IsNullOrWhiteSpace(rawList) || string.IsNullOrWhiteSpace(fromAddress)) return false;

        var from = fromAddress.Trim().ToLowerInvariant();
        var at = from.LastIndexOf('@');
        if (at <= 0 || at == from.Length - 1) return false;
        var domain = from[(at + 1)..];

        foreach (var token in rawList.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = token.Trim().ToLowerInvariant();
            var entryAt = entry.IndexOf('@');
            if (entryAt > 0)
            {
                if (entry == from) return true;          // exact address
            }
            else
            {
                var entryDomain = entryAt == 0 ? entry[1..] : entry;
                if (entryDomain.Length > 0 && entryDomain == domain) return true; // exact domain
            }
        }
        return false;
    }
}
