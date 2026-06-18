using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// <inheritdoc cref="IClaudeAssistService"/>
public sealed class ClaudeAssistService : IClaudeAssistService
{
    /// Hard ceiling per image regardless of settings — a defensive bound on
    /// the request body. Larger attachments are skipped rather than sent.
    private const int MaxImageBytes = 5 * 1024 * 1024;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;
    private readonly IClaudeApiClient _api;
    private readonly IClaudeUsageStore _usage;
    private readonly IAttachmentRepository _attachments;
    private readonly ITicketRepository _tickets;
    private readonly IMailMessageRepository _mail;
    private readonly IBlobStore _blobs;
    private readonly ILogger<ClaudeAssistService> _logger;

    public ClaudeAssistService(
        NpgsqlDataSource dataSource,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IClaudeApiClient api,
        IClaudeUsageStore usage,
        IAttachmentRepository attachments,
        ITicketRepository tickets,
        IMailMessageRepository mail,
        IBlobStore blobs,
        ILogger<ClaudeAssistService> logger)
    {
        _dataSource = dataSource;
        _settings = settings;
        _secrets = secrets;
        _api = api;
        _usage = usage;
        _attachments = attachments;
        _tickets = tickets;
        _mail = mail;
        _blobs = blobs;
        _logger = logger;
    }

    public async Task<ClaudeProposalResult> GenerateProposalAsync(
        Guid ticketId,
        Guid userId,
        IReadOnlyList<Guid> selectedAttachmentIds,
        CancellationToken ct)
    {
        var monthStartUtc = MonthStartUtc(DateTime.UtcNow);

        // ---- Guards (no API call, no cost) -----------------------------
        var enabled = await _settings.GetAsync<bool>(SettingKeys.Claude.Enabled, ct);
        if (!enabled)
            return await BlockedAsync(userId, ticketId, "disabled", ClaudeProposalOutcome.Disabled,
                "The Claude AI assistant is turned off.", monthStartUtc, ct);

        var hasKey = await _secrets.HasAsync(ProtectedSecretKeys.ClaudeApiKey, ct);
        if (!hasKey)
            return await BlockedAsync(userId, ticketId, "not_configured", ClaudeProposalOutcome.NotConfigured,
                "The Claude AI assistant has no API key configured.", monthStartUtc, ct);

        var zdrConfirmed = await _settings.GetAsync<bool>(SettingKeys.Claude.ZeroDataRetentionConfirmed, ct);
        if (!zdrConfirmed)
            return await BlockedAsync(userId, ticketId, "zdr_not_confirmed", ClaudeProposalOutcome.ZdrNotConfirmed,
                "Zero data retention has not been confirmed for the Claude organisation.", monthStartUtc, ct);

        var effectiveBudgetCents = await ResolveBudgetCentsAsync(userId, ct);
        var budgetMicro = (long)effectiveBudgetCents * 10_000L;
        if (effectiveBudgetCents <= 0)
            return await BlockedAsync(userId, ticketId, "no_budget", ClaudeProposalOutcome.NoBudget,
                "You have no Claude AI budget assigned.", monthStartUtc, ct);

        var spendMicro = await _usage.GetMonthSpendMicroEurAsync(userId, monthStartUtc, ct);
        if (spendMicro >= budgetMicro)
        {
            await _usage.LogAsync(new ClaudeUsageEntry(userId, ticketId, "", 0, 0, 0, 0, "blocked", "budget_exceeded", null), ct);
            return new ClaudeProposalResult(ClaudeProposalOutcome.BudgetExceeded, null, null,
                "Your monthly Claude AI budget is exhausted.", 0, 0, 0, 0, spendMicro, budgetMicro);
        }

        // ---- Build the scoped prompt -----------------------------------
        var systemPrompt = await _settings.GetAsync<string>(SettingKeys.Claude.SystemPrompt, ct) ?? string.Empty;
        var maxContextChars = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.MaxContextChars, ct), 1000, 200_000);
        var userText = await BuildTicketContextAsync(ticketId, maxContextChars, ct);

        var images = await ResolveImagesAsync(ticketId, selectedAttachmentIds, ct);

        // ---- Call + log -------------------------------------------------
        try
        {
            var result = await _api.CreateProposalAsync(systemPrompt, userText, images, ct);

            var inputPriceCents = await _settings.GetAsync<int>(SettingKeys.Claude.InputPriceCentsPerMTok, ct);
            var outputPriceCents = await _settings.GetAsync<int>(SettingKeys.Claude.OutputPriceCentsPerMTok, ct);
            var costMicro = ((long)result.InputTokens * inputPriceCents + (long)result.OutputTokens * outputPriceCents) / 100L;

            var refused = string.Equals(result.StopReason, "refusal", StringComparison.OrdinalIgnoreCase);

            await _usage.LogAsync(new ClaudeUsageEntry(
                userId, ticketId, result.Model, result.InputTokens, result.OutputTokens,
                costMicro, images.Count, "ok", refused ? "refusal" : null, result.RequestId), ct);

            var newSpend = spendMicro + costMicro;

            if (refused || string.IsNullOrWhiteSpace(result.Text))
            {
                return new ClaudeProposalResult(
                    ClaudeProposalOutcome.Refused, null, null,
                    "The assistant declined to produce a proposal for this ticket's content.",
                    result.InputTokens, result.OutputTokens, costMicro, images.Count, newSpend, budgetMicro);
            }

            return new ClaudeProposalResult(
                ClaudeProposalOutcome.Ok,
                result.Text,
                MarkdownToHtml(result.Text),
                null,
                result.InputTokens, result.OutputTokens, costMicro, images.Count, newSpend, budgetMicro);
        }
        catch (ClaudeApiException ex)
        {
            // The client already wrote an integration_audit row; record the
            // failed attempt in the usage log too (zero cost — a failed call
            // is not billed) so it is visible in the per-agent overview.
            await _usage.LogAsync(new ClaudeUsageEntry(
                userId, ticketId, "", 0, 0, 0, images.Count, "error",
                ex.UpstreamErrorCode ?? "api_error", null), ct);
            throw;
        }
    }

    // ---- Budget --------------------------------------------------------

    private async Task<int> ResolveBudgetCentsAsync(Guid userId, CancellationToken ct)
    {
        var overrideCents = await _usage.GetUserBudgetOverrideCentsAsync(userId, ct);
        if (overrideCents.HasValue) return overrideCents.Value;
        return await _settings.GetAsync<int>(SettingKeys.Claude.DefaultMonthlyBudgetEurCents, ct);
    }

    private static DateTime MonthStartUtc(DateTime nowUtc) =>
        new(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<ClaudeProposalResult> BlockedAsync(
        Guid userId, Guid ticketId, string code, ClaudeProposalOutcome outcome,
        string message, DateTime monthStartUtc, CancellationToken ct)
    {
        await _usage.LogAsync(new ClaudeUsageEntry(userId, ticketId, "", 0, 0, 0, 0, "blocked", code, null), ct);
        var spend = await _usage.GetMonthSpendMicroEurAsync(userId, monthStartUtc, ct);
        return new ClaudeProposalResult(outcome, null, null, message, 0, 0, 0, 0, spend, 0);
    }

    // ---- Ticket context ------------------------------------------------

    private async Task<string> BuildTicketContextAsync(Guid ticketId, int maxChars, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        const string headSql = """
            SELECT t.number AS Number, t.subject AS Subject, COALESCE(b.body_text, '') AS Body
              FROM tickets t
              LEFT JOIN ticket_bodies b ON b.ticket_id = t.id
             WHERE t.id = @id
            """;
        var head = await conn.QuerySingleOrDefaultAsync<TicketHeadRow>(
            new CommandDefinition(headSql, new { id = ticketId }, cancellationToken: ct));

        const string eventsSql = """
            SELECT event_type        AS EventType,
                   body_text         AS BodyText,
                   is_internal       AS IsInternal,
                   author_user_id    AS AuthorUserId,
                   author_contact_id AS AuthorContactId,
                   created_utc       AS CreatedUtc
              FROM ticket_events
             WHERE ticket_id = @id
               AND body_text IS NOT NULL AND body_text <> ''
             ORDER BY created_utc ASC, id ASC
             LIMIT 200
            """;
        var events = (await conn.QueryAsync<TicketEventRow>(
            new CommandDefinition(eventsSql, new { id = ticketId }, cancellationToken: ct))).ToList();

        var sb = new StringBuilder();
        sb.Append("Here is the full content of the ticket to resolve.\n\n");
        if (head is not null)
        {
            sb.Append("Ticket #").Append(head.Number).Append('\n');
            sb.Append("Subject: ").Append(head.Subject).Append("\n\n");
            sb.Append("Description:\n").Append(Clamp(head.Body, 8000)).Append("\n\n");
        }

        if (events.Count > 0)
        {
            sb.Append("Conversation and internal notes (oldest first):\n");
            foreach (var e in events)
            {
                var who = e.AuthorContactId.HasValue ? "Customer"
                    : e.AuthorUserId.HasValue ? "Agent"
                    : "System";
                var note = e.IsInternal ? " (internal note)" : "";
                sb.Append("[").Append(who).Append(note).Append("] ")
                  .Append(e.EventType).Append(": ")
                  .Append(Clamp(e.BodyText ?? string.Empty, 2000)).Append('\n');
                if (sb.Length >= maxChars) break;
            }
        }

        return Clamp(sb.ToString(), maxChars);
    }

    private static string Clamp(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "\n…[truncated]";
    }

    // ---- Images --------------------------------------------------------

    private async Task<IReadOnlyList<ClaudeImageInput>> ResolveImagesAsync(
        Guid ticketId, IReadOnlyList<Guid> selectedAttachmentIds, CancellationToken ct)
    {
        if (selectedAttachmentIds.Count == 0) return Array.Empty<ClaudeImageInput>();

        var imagesEnabled = await _settings.GetAsync<bool>(SettingKeys.Claude.ImagesEnabled, ct);
        if (!imagesEnabled) return Array.Empty<ClaudeImageInput>();

        var maxImages = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.MaxImages, ct), 0, 20);
        if (maxImages == 0) return Array.Empty<ClaudeImageInput>();

        var result = new List<ClaudeImageInput>();
        foreach (var attId in selectedAttachmentIds.Distinct())
        {
            if (result.Count >= maxImages) break;

            var att = await _attachments.GetByIdAsync(attId, ct);
            if (att is null) continue;
            if (att.ProcessingState != "Ready" || string.IsNullOrWhiteSpace(att.ContentHash)) continue;
            if (string.IsNullOrWhiteSpace(att.MimeType) || !att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;
            if (att.SizeBytes > MaxImageBytes) continue;

            // Ownership: staged directly on this ticket, attached to an event
            // that belongs to this ticket, or an inbound-mail attachment
            // (incl. inline images) on a mail linked to this ticket. Mirrors
            // the two attachment download endpoints so an id from another
            // ticket can't be smuggled in.
            var ownsDirect = att.OwnerKind == "Ticket" && att.OwnerId == ticketId && att.EventId is null;
            var ownsViaEvent = att.EventId.HasValue && await _tickets.EventBelongsToTicketAsync(ticketId, att.EventId.Value, ct);
            var ownsViaMail = false;
            if (!ownsDirect && !ownsViaEvent && att.OwnerKind == "Mail")
            {
                var mailRow = await _mail.GetByIdAsync(att.OwnerId, ct);
                ownsViaMail = mailRow is not null && mailRow.TicketId == ticketId;
            }
            if (!ownsDirect && !ownsViaEvent && !ownsViaMail) continue;

            var stream = await _blobs.OpenReadAsync(att.ContentHash!, ct);
            if (stream is null) continue;
            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                if (ms.Length == 0 || ms.Length > MaxImageBytes) continue;
                var base64 = System.Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
                result.Add(new ClaudeImageInput(att.MimeType, base64));
            }
        }

        return result;
    }

    // ---- Markdown rendering --------------------------------------------

    /// Renders the model's markdown proposal into a small, safe subset of
    /// HTML that drops straight into the Tiptap draft editor: headings, bold,
    /// italic, inline & fenced code, bullet/numbered lists, block quotes,
    /// horizontal rules and paragraphs. Every piece of text is HTML-escaped;
    /// no raw markup from the model is ever trusted — only structure the
    /// parser itself recognises becomes a tag, so the model cannot inject
    /// arbitrary HTML. The model emits markdown (see the system prompt), and
    /// without this it would land in the editor as literal '#', '**', '-'.
    /// Internal for unit tests (escaping is security-sensitive).
    internal static string MarkdownToHtml(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var i = 0;

        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();

            // Blank line — just a block separator.
            if (trimmed.Length == 0) { i++; continue; }

            // Fenced code block: ``` … ```
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                i++;
                var code = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Append(Escape(lines[i])).Append('\n');
                    i++;
                }
                if (i < lines.Length) i++; // skip the closing fence
                sb.Append("<pre><code>").Append(code.ToString().TrimEnd('\n')).Append("</code></pre>");
                continue;
            }

            // Horizontal rule: ---, ***, ___ (3+ of the same char, alone).
            if (IsHorizontalRule(trimmed)) { sb.Append("<hr />"); i++; continue; }

            // Heading: #..###### text
            var level = HeadingLevel(trimmed);
            if (level > 0)
            {
                var tag = "h" + Math.Clamp(level, 2, 4);
                var content = trimmed[level..].TrimStart('#', ' ');
                sb.Append('<').Append(tag).Append('>')
                  .Append(InlineToHtml(content))
                  .Append("</").Append(tag).Append('>');
                i++;
                continue;
            }

            // Unordered list.
            if (IsBullet(trimmed))
            {
                sb.Append("<ul>");
                while (i < lines.Length && IsBullet(lines[i].Trim()))
                {
                    sb.Append("<li>").Append(InlineToHtml(StripBullet(lines[i].Trim()))).Append("</li>");
                    i++;
                }
                sb.Append("</ul>");
                continue;
            }

            // Ordered list.
            if (IsOrdered(trimmed))
            {
                sb.Append("<ol>");
                while (i < lines.Length && IsOrdered(lines[i].Trim()))
                {
                    sb.Append("<li>").Append(InlineToHtml(StripOrdered(lines[i].Trim()))).Append("</li>");
                    i++;
                }
                sb.Append("</ol>");
                continue;
            }

            // Block quote.
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new StringBuilder();
                var first = true;
                while (i < lines.Length && lines[i].Trim().StartsWith(">", StringComparison.Ordinal))
                {
                    var q = lines[i].Trim();
                    q = q.Length > 1 ? q[1..].TrimStart() : string.Empty;
                    if (!first) quote.Append("<br />");
                    quote.Append(InlineToHtml(q));
                    first = false;
                    i++;
                }
                sb.Append("<blockquote><p>").Append(quote).Append("</p></blockquote>");
                continue;
            }

            // Paragraph: gather consecutive "plain" lines, soft-wrapped.
            var para = new StringBuilder();
            var firstLine = true;
            while (i < lines.Length)
            {
                var l = lines[i].Trim();
                if (l.Length == 0 || IsHorizontalRule(l) || HeadingLevel(l) > 0 ||
                    IsBullet(l) || IsOrdered(l) ||
                    l.StartsWith(">", StringComparison.Ordinal) ||
                    l.StartsWith("```", StringComparison.Ordinal))
                    break;
                if (!firstLine) para.Append("<br />");
                para.Append(InlineToHtml(l));
                firstLine = false;
                i++;
            }
            sb.Append("<p>").Append(para).Append("</p>");
        }

        return sb.Length == 0 ? "<p></p>" : sb.ToString();
    }

    /// Applies inline markdown (code, bold, italic) to a single run of text.
    /// The text is HTML-escaped first; code spans are stashed behind a NUL
    /// sentinel so emphasis markers inside them are left untouched.
    private static string InlineToHtml(string raw)
    {
        var s = Escape(raw);

        var codeSpans = new List<string>();
        s = Regex.Replace(s, "`([^`]+)`", m =>
        {
            codeSpans.Add(m.Groups[1].Value);
            return " " + (codeSpans.Count - 1) + " ";
        });

        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"\*(.+?)\*", "<em>$1</em>");

        s = Regex.Replace(s, " (\\d+) ",
            m => "<code>" + codeSpans[int.Parse(m.Groups[1].Value)] + "</code>");

        return s;
    }

    private static bool IsHorizontalRule(string s) =>
        s.Length >= 3 && (s.All(c => c == '-') || s.All(c => c == '*') || s.All(c => c == '_'));

    private static int HeadingLevel(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == '#') n++;
        return n is >= 1 and <= 6 && n < s.Length && s[n] == ' ' ? n : 0;
    }

    private static bool IsBullet(string s) =>
        s.Length >= 2 && s[0] is '-' or '*' or '+' && s[1] == ' ';

    private static string StripBullet(string s) => s[2..].TrimStart();

    private static bool IsOrdered(string s)
    {
        var n = 0;
        while (n < s.Length && char.IsDigit(s[n])) n++;
        return n > 0 && n + 1 < s.Length && s[n] is '.' or ')' && s[n + 1] == ' ';
    }

    private static string StripOrdered(string s)
    {
        var n = 0;
        while (n < s.Length && char.IsDigit(s[n])) n++;
        return s[(n + 1)..].TrimStart();
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private sealed class TicketHeadRow
    {
        public long Number { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }

    private sealed class TicketEventRow
    {
        public string EventType { get; set; } = string.Empty;
        public string? BodyText { get; set; }
        public bool IsInternal { get; set; }
        public Guid? AuthorUserId { get; set; }
        public Guid? AuthorContactId { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
