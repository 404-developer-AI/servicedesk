using System.Net;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Portal;

/// Which transactional portal mail to send; each maps onto a subject/body
/// template pair under Settings → Portal.
public enum PortalMailKind
{
    EmailVerification,
    Invitation,
    PasswordReset,
    Approved,
}

public interface IPortalMailService
{
    /// Resolves the sender mailbox (Portal.FromMailbox → registration queue →
    /// new-ticket queue). Null = nothing configured; mail cannot be sent.
    Task<string?> ResolveFromMailboxAsync(CancellationToken ct);

    /// Renders the template for <paramref name="kind"/> and sends it.
    /// Returns false (and logs) when the sender mailbox is missing or Graph
    /// refused the send — callers decide whether that is fatal.
    Task<bool> SendAsync(
        PortalMailKind kind, string toEmail, string toName, string link, TimeSpan? validity, CancellationToken ct);
}

public sealed class PortalMailService : IPortalMailService
{
    private readonly IGraphMailClient _graph;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ISettingsService _settings;
    private readonly ILogger<PortalMailService> _logger;

    public PortalMailService(
        IGraphMailClient graph,
        ITaxonomyRepository taxonomy,
        ISettingsService settings,
        ILogger<PortalMailService> logger)
    {
        _graph = graph;
        _taxonomy = taxonomy;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string?> ResolveFromMailboxAsync(CancellationToken ct)
    {
        var explicitFrom = await _settings.GetAsync<string>(SettingKeys.Portal.FromMailbox, ct);
        if (!string.IsNullOrWhiteSpace(explicitFrom)) return explicitFrom.Trim();

        foreach (var key in new[] { SettingKeys.Portal.RegistrationQueueId, SettingKeys.Portal.NewTicketQueueId })
        {
            var raw = await _settings.GetAsync<string>(key, ct);
            if (!Guid.TryParse(raw, out var queueId)) continue;
            var queue = await _taxonomy.GetQueueAsync(queueId, ct);
            var mailbox = FirstNonEmpty(queue?.OutboundMailboxAddress, queue?.InboundMailboxAddress);
            if (mailbox is not null) return mailbox;
        }
        return null;
    }

    public async Task<bool> SendAsync(
        PortalMailKind kind, string toEmail, string toName, string link, TimeSpan? validity, CancellationToken ct)
    {
        var from = await ResolveFromMailboxAsync(ct);
        if (from is null)
        {
            _logger.LogError(
                "Portal mail ({Kind}) to {Email} not sent: no sender mailbox configured (Portal.FromMailbox / queue mailbox).",
                kind, toEmail);
            return false;
        }

        var (subjectKey, bodyKey) = kind switch
        {
            PortalMailKind.EmailVerification => (SettingKeys.Portal.VerificationMailSubject, SettingKeys.Portal.VerificationMailBody),
            PortalMailKind.Invitation => (SettingKeys.Portal.InvitationMailSubject, SettingKeys.Portal.InvitationMailBody),
            PortalMailKind.PasswordReset => (SettingKeys.Portal.PasswordResetMailSubject, SettingKeys.Portal.PasswordResetMailBody),
            PortalMailKind.Approved => (SettingKeys.Portal.ApprovedMailSubject, SettingKeys.Portal.ApprovedMailBody),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var subjectTemplate = await _settings.GetAsync<string>(subjectKey, ct) ?? string.Empty;
        var bodyTemplate = await _settings.GetAsync<string>(bodyKey, ct) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(subjectTemplate))
        {
            // Empty subject = this mail is switched off (documented for the
            // approval mail; harmless for the others).
            return true;
        }

        var organisation = await OrganisationNameAsync(ct);
        var fromName = await _settings.GetAsync<string>(SettingKeys.Portal.FromName, ct);
        if (string.IsNullOrWhiteSpace(fromName)) fromName = organisation;

        // Every value is HTML-escaped before substitution; the link is a URL
        // we built ourselves but is escaped too so an attribute context is
        // always safe.
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{name}}"] = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(toName) ? toEmail : toName),
            ["{{email}}"] = WebUtility.HtmlEncode(toEmail),
            ["{{link}}"] = WebUtility.HtmlEncode(link),
            ["{{expires}}"] = WebUtility.HtmlEncode(DescribeValidity(validity)),
            ["{{organisation}}"] = WebUtility.HtmlEncode(organisation),
        };
        var subject = ApplyTokens(subjectTemplate, tokens, html: false);
        var body = ApplyTokens(bodyTemplate, tokens, html: true);

        var message = new GraphOutboundMessage(
            FromMailbox: from,
            Subject: subject,
            BodyHtml: body,
            To: new[] { new GraphRecipient(toEmail, toName) },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            // No Reply-To on purpose: a reply to a system mail must not open a ticket.
            ReplyTo: Array.Empty<GraphRecipient>(),
            InternetMessageHeaders: new[]
            {
                new GraphOutboundHeader("X-Auto-Submitted", "auto-generated"),
                new GraphOutboundHeader("X-Servicedesk-Portal-Mail", kind.ToString()),
            });
        _ = fromName; // Graph sends as the mailbox's own display name; kept for future SMTP path.

        try
        {
            await _graph.SendMailAsync(message, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Portal mail ({Kind}) to {Email} failed to send via Graph.", kind, toEmail);
            return false;
        }
    }

    private async Task<string> OrganisationNameAsync(CancellationToken ct)
    {
        var name = await _settings.GetAsync<string>(SettingKeys.Portal.OrganisationName, ct);
        return string.IsNullOrWhiteSpace(name) ? "Servicedesk" : name.Trim();
    }

    internal static string ApplyTokens(string template, IReadOnlyDictionary<string, string> tokens, bool html)
    {
        var result = template;
        foreach (var (token, value) in tokens)
        {
            // Subjects are plain text: undo the HTML escaping applied above.
            var v = html ? value : WebUtility.HtmlDecode(value);
            result = result.Replace(token, v, StringComparison.Ordinal);
        }
        return result;
    }

    internal static string DescribeValidity(TimeSpan? validity)
    {
        if (validity is null) return string.Empty;
        var v = validity.Value;
        if (v.TotalHours >= 48 && v.TotalHours % 24 == 0) return $"{(int)(v.TotalHours / 24)} days";
        if (v.TotalHours >= 1) return v.TotalHours == 1 ? "1 hour" : $"{(int)Math.Round(v.TotalHours)} hours";
        return v.TotalMinutes <= 1 ? "1 minute" : $"{(int)Math.Round(v.TotalMinutes)} minutes";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
