using System.Net.Mail;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Reports;
using Servicedesk.Infrastructure.Integrations.M365;
using Servicedesk.Infrastructure.Mail.Graph;

namespace Servicedesk.Infrastructure.Contracts.Reports;

/// Who is sending (resolved from the HTTP principal by the endpoint). Kept out
/// of the request records so the same preview/send shape is reusable by the
/// future bulk-send path.
public sealed record ReportActor(Guid? UserId, string Name, string? Email);

/// Inputs for a single-company report preview/send. Column + scope overrides
/// are optional — null means "use the template's saved selection". A non-null
/// <see cref="Recipients"/> overrides the company's reporting contacts.
public sealed record ReportSendInput(
    Guid CompanyId,
    Guid TemplateId,
    IReadOnlyList<string>? Columns,
    string? Scope,
    IReadOnlyList<ReportRecipient>? Recipients);

public sealed class ReportPreviewResult
{
    public string Subject { get; init; } = string.Empty;
    public string BodyHtml { get; init; } = string.Empty;
    public IReadOnlyList<ReportRecipient> Recipients { get; init; } = Array.Empty<ReportRecipient>();
    public string? FromAddress { get; init; }
    public bool AttachPdf { get; init; }
    public string Scope { get; init; } = ReportScope.All;
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public int MailboxCount { get; init; }
    public int? SpamProtected { get; init; }
    public int? SpamTotal { get; init; }
    public int? ExchangeProtected { get; init; }
    public int? ExchangeTotal { get; init; }
    public int? OneDriveProtected { get; init; }
    public int? OneDriveTotal { get; init; }
    /// Non-blocking advisories surfaced in the send modal (no reporting
    /// contacts, queue has no mailbox, integration off, …).
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record ReportSendResult(bool Ok, string? Error, Guid? SendId, string? InternetMessageId);

public interface IM365ReportSender
{
    /// Resolve + render everything for the send modal, without sending.
    /// Returns null only when the template does not exist.
    Task<ReportPreviewResult?> PreviewAsync(ReportSendInput input, CancellationToken ct);

    /// Render, generate the PDF, send via Graph and append the send log.
    /// Validation failures come back as <c>Ok=false</c> with an error message;
    /// the only nulls are a missing template (handled by the caller as 404).
    Task<ReportSendResult> SendAsync(ReportSendInput input, ReportActor actor, CancellationToken ct);
}

public sealed class M365ReportSender : IM365ReportSender
{
    private const int MaxRecipients = 50;

    private readonly IReportTemplateRepository _templates;
    private readonly IM365MatchingService _matching;
    private readonly IReportingContactStore _reportingContacts;
    private readonly IM365ReportSendLog _sendLog;
    private readonly IGraphMailClient _graph;
    private readonly NpgsqlDataSource _dataSource;

    public M365ReportSender(
        IReportTemplateRepository templates,
        IM365MatchingService matching,
        IReportingContactStore reportingContacts,
        IM365ReportSendLog sendLog,
        IGraphMailClient graph,
        NpgsqlDataSource dataSource)
    {
        _templates = templates;
        _matching = matching;
        _reportingContacts = reportingContacts;
        _sendLog = sendLog;
        _graph = graph;
        _dataSource = dataSource;
    }

    public async Task<ReportPreviewResult?> PreviewAsync(ReportSendInput input, CancellationToken ct)
    {
        var ctx = await BuildAsync(input, actor: null, ct);
        if (ctx is null) return null;

        return new ReportPreviewResult
        {
            Subject = ctx.Subject,
            BodyHtml = ctx.BodyHtml,
            Recipients = ctx.Recipients,
            FromAddress = ctx.FromAddress,
            AttachPdf = ctx.Template.AttachPdf,
            Scope = ctx.Scope,
            Columns = ctx.Render.Columns,
            MailboxCount = ctx.Render.MailboxCount,
            SpamProtected = ctx.Render.SpamProtected,
            SpamTotal = ctx.Render.SpamTotal,
            ExchangeProtected = ctx.Render.ExchangeProtected,
            ExchangeTotal = ctx.Render.ExchangeTotal,
            OneDriveProtected = ctx.Render.OneDriveProtected,
            OneDriveTotal = ctx.Render.OneDriveTotal,
            Warnings = ctx.Warnings,
        };
    }

    public async Task<ReportSendResult> SendAsync(ReportSendInput input, ReportActor actor, CancellationToken ct)
    {
        var ctx = await BuildAsync(input, actor, ct);
        if (ctx is null) return new ReportSendResult(false, "Template not found.", null, null);

        // Hard pre-conditions for an actual send.
        if (string.IsNullOrWhiteSpace(ctx.FromAddress))
            return new ReportSendResult(false, "The template's queue has no outbound mailbox configured.", null, null);
        if (ctx.Recipients.Count == 0)
            return new ReportSendResult(false, "No valid recipients. Designate at least one reporting contact or add a recipient.", null, null);

        var recipients = ctx.Recipients.Select(r => new GraphRecipient(r.Address, r.Name)).ToList();

        try
        {
            IReadOnlyList<GraphOutboundAttachment>? attachments = null;
            if (ctx.Template.AttachPdf)
            {
                var pdf = M365ReportPdfGenerator.Generate(BuildPdfData(ctx, actor));
                var fileName = BuildPdfFileName(ctx.View);
                attachments = new[]
                {
                    GraphOutboundAttachment.FromBytes(fileName, "application/pdf", pdf, isInline: false, contentId: null),
                };
            }

            var message = new GraphOutboundMessage(
                FromMailbox: ctx.FromAddress!,
                Subject: ctx.Subject,
                BodyHtml: ctx.BodyHtml,
                To: recipients,
                Cc: Array.Empty<GraphRecipient>(),
                Bcc: Array.Empty<GraphRecipient>(),
                // Replies go back to the sending queue's mailbox, where the
                // poller turns them into tickets.
                ReplyTo: new[] { new GraphRecipient(ctx.FromAddress!, ctx.FromName) },
                Attachments: attachments);

            var sent = await _graph.SendMailAsync(message, ct);

            var sendId = await _sendLog.InsertAsync(BuildLogEntry(ctx, actor, "sent", sent.InternetMessageId, null), ct);
            return new ReportSendResult(true, null, sendId, sent.InternetMessageId);
        }
        catch (Exception ex)
        {
            // Record the failed attempt so the history shows it, then surface a
            // safe message (Graph error details stay server-side).
            await _sendLog.InsertAsync(BuildLogEntry(ctx, actor, "failed", null, Truncate(ex.Message, 1000)), ct);
            return new ReportSendResult(false, "Sending failed. Check the Microsoft 365 mail configuration and try again.", null, null);
        }
    }

    // ── Shared build pipeline ────────────────────────────────────────────

    private sealed class BuildContext
    {
        public required ReportTemplate Template { get; init; }
        public required M365CompanyMailboxView View { get; init; }
        public required ReportRenderResult Render { get; init; }
        public required string Subject { get; init; }
        public required string BodyHtml { get; init; }
        public required string Scope { get; init; }
        public required IReadOnlyList<ReportRecipient> Recipients { get; init; }
        public string? FromAddress { get; init; }
        public string FromName { get; init; } = "Servicedesk";
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    private async Task<BuildContext?> BuildAsync(ReportSendInput input, ReportActor? actor, CancellationToken ct)
    {
        var template = await _templates.GetAsync(input.TemplateId, ct);
        if (template is null) return null;

        var columns = ReportColumns.Normalize(input.Columns ?? template.Columns);
        var scope = ReportScope.IsValid(input.Scope) ? input.Scope! : template.Scope;

        var view = await _matching.GetCompanyViewAsync(input.CompanyId, ct);
        var render = M365ReportRenderer.Render(view, columns, scope);

        var warnings = new List<string>();

        // FROM mailbox from the template's queue.
        string? fromAddress = null;
        var fromName = "Servicedesk";
        if (template.QueueId is Guid queueId)
        {
            var (addr, name) = await ResolveQueueMailboxAsync(queueId, ct);
            fromAddress = addr;
            if (!string.IsNullOrWhiteSpace(name)) fromName = name!;
        }
        if (string.IsNullOrWhiteSpace(fromAddress))
            warnings.Add("This template has no sending mailbox — set a queue with an outbound address on the template.");

        // Recipients: explicit override, else the company's reporting contacts.
        IReadOnlyList<ReportRecipient> recipients;
        if (input.Recipients is not null)
        {
            recipients = SanitizeRecipients(input.Recipients);
        }
        else
        {
            recipients = SanitizeRecipients(await _reportingContacts.GetReportingRecipientsAsync(input.CompanyId, ct));
            if (recipients.Count == 0)
                warnings.Add("No reporting contacts are set for this company. Designate at least one before sending.");
        }

        if (!view.SpamFilterAvailable && !view.VeeamAvailable)
            warnings.Add("Neither the spam-filter nor backup integration is matched for this company — protection columns will read 'n/a'.");

        // Token substitution.
        var nowUtc = DateTime.UtcNow;
        var tz = ServerTz();
        var scalars = BuildScalars(view, render, actor, nowUtc, tz);

        var subject = M365ReportRenderer.ApplyScalarTokens(template.Subject, scalars);
        if (string.IsNullOrWhiteSpace(subject))
            subject = $"Microsoft 365 report — {view.CompanyName}".Trim();

        var body = M365ReportRenderer.ApplyScalarTokens(template.BodyHtml, scalars);
        body = body.Contains(ReportTokens.Table)
            ? body.Replace(ReportTokens.Table, render.TableHtml)
            // No explicit placeholder — append the overview so it always appears.
            : body + "<br/>" + render.TableHtml;

        return new BuildContext
        {
            Template = template,
            View = view,
            Render = render,
            Subject = subject,
            BodyHtml = body,
            Scope = scope,
            Recipients = recipients,
            FromAddress = fromAddress,
            FromName = fromName,
            Warnings = warnings,
        };
    }

    private static Dictionary<string, string> BuildScalars(
        M365CompanyMailboxView view, ReportRenderResult render, ReportActor? actor, DateTime nowUtc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{company.name}}"] = view.CompanyName ?? "",
            ["{{company.code}}"] = view.CompanyCode ?? "",
            ["{{report.date}}"] = local.ToString("yyyy-MM-dd"),
            ["{{report.mailboxCount}}"] = render.MailboxCount.ToString(),
            ["{{report.spamProtected}}"] = render.SpamProtected?.ToString() ?? "n/a",
            ["{{report.onedriveProtected}}"] = render.OneDriveProtected?.ToString() ?? "n/a",
            ["{{report.exchangeProtected}}"] = render.ExchangeProtected?.ToString() ?? "n/a",
            ["{{user.name}}"] = actor?.Name ?? "",
            ["{{user.email}}"] = actor?.Email ?? "",
        };
    }

    private static M365ReportPdfData BuildPdfData(BuildContext ctx, ReportActor actor) => new(
        CompanyName: ctx.View.CompanyName ?? "",
        CompanyCode: ctx.View.CompanyCode,
        GeneratedUtc: DateTime.UtcNow,
        GeneratedBy: actor.Name,
        ServerTimezoneId: ServerTz().Id,
        Scope: ctx.Scope,
        Columns: ctx.Render.Columns,
        Rows: ctx.Render.Rows,
        MailboxCount: ctx.Render.MailboxCount,
        SpamProtected: ctx.Render.SpamProtected,
        SpamTotal: ctx.Render.SpamTotal,
        ExchangeProtected: ctx.Render.ExchangeProtected,
        ExchangeTotal: ctx.Render.ExchangeTotal,
        OneDriveProtected: ctx.Render.OneDriveProtected,
        OneDriveTotal: ctx.Render.OneDriveTotal);

    private static M365ReportSendEntry BuildLogEntry(
        BuildContext ctx, ReportActor actor, string status, string? internetMessageId, string? error) => new()
    {
        CompanyId = ctx.View.CompanyId,
        TemplateId = ctx.Template.Id,
        TemplateName = ctx.Template.Name,
        SentBy = actor.UserId,
        SentByName = actor.Name,
        Subject = ctx.Subject,
        Recipients = ctx.Recipients,
        Columns = ctx.Render.Columns,
        Scope = ctx.Scope,
        MailboxCount = ctx.Render.MailboxCount,
        SpamProtected = ctx.Render.SpamProtected,
        ExchangeProtected = ctx.Render.ExchangeProtected,
        OneDriveProtected = ctx.Render.OneDriveProtected,
        InternetMessageId = internetMessageId,
        Status = status,
        Error = error,
    };

    private async Task<(string? Address, string? Name)> ResolveQueueMailboxAsync(Guid queueId, CancellationToken ct)
    {
        const string sql = """
            SELECT COALESCE(NULLIF(outbound_mailbox_address::text, ''), inbound_mailbox_address::text) AS address,
                   name AS name
              FROM queues
             WHERE id = @queueId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<QueueMailboxRow>(
            new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
        var address = string.IsNullOrWhiteSpace(row?.Address) ? null : row!.Address!.Trim();
        return (address, row?.Name);
    }

    private sealed class QueueMailboxRow
    {
        public string? Address { get; set; }
        public string? Name { get; set; }
    }

    /// Drop blanks/invalids, de-dupe by address (case-insensitive), cap count.
    private static IReadOnlyList<ReportRecipient> SanitizeRecipients(IEnumerable<ReportRecipient> input)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ReportRecipient>();
        foreach (var r in input)
        {
            var addr = r.Address?.Trim();
            if (string.IsNullOrWhiteSpace(addr) || !IsValidEmail(addr)) continue;
            if (!seen.Add(addr)) continue;
            result.Add(new ReportRecipient(addr, (r.Name ?? "").Trim()));
            if (result.Count >= MaxRecipients) break;
        }
        return result;
    }

    private static bool IsValidEmail(string address)
    {
        try
        {
            var addr = new MailAddress(address);
            return addr.Address == address;
        }
        catch
        {
            return false;
        }
    }

    private static TimeZoneInfo ServerTz() => TimeZoneInfo.Local;

    private static string BuildPdfFileName(M365CompanyMailboxView view)
    {
        var basis = !string.IsNullOrWhiteSpace(view.CompanyCode)
            ? view.CompanyCode!
            : (view.CompanyName ?? "company");
        var safe = new string(basis.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        if (safe.Length == 0) safe = "company";
        return $"m365-report-{safe}-{DateTime.UtcNow:yyyyMMdd}.pdf";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
