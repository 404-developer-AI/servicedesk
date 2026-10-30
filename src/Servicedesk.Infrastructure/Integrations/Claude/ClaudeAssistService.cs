using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Health;
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

    public Task<ClaudeProposalResult> GenerateProposalAsync(
        Guid ticketId,
        Guid userId,
        IReadOnlyList<Guid> selectedAttachmentIds,
        CancellationToken ct) =>
        RunAsync(ClaudeFeatures.Proposal, ticketId, userId, async () =>
        {
            var systemPrompt = await _settings.GetAsync<string>(SettingKeys.Claude.SystemPrompt, ct) ?? string.Empty;
            var maxContextChars = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.MaxContextChars, ct), 1000, 200_000);
            var userText = await BuildTicketContextAsync(
                ticketId, "Here is the full content of the ticket to resolve.", maxContextChars, ct);
            var images = await ResolveImagesAsync(ticketId, selectedAttachmentIds, ct);
            return (systemPrompt, userText, images);
        }, ct);

    public Task<ClaudeProposalResult> GenerateSummaryAsync(
        Guid ticketId,
        Guid userId,
        CancellationToken ct) =>
        RunAsync(ClaudeFeatures.Summary, ticketId, userId, async () =>
        {
            var instructions = await _settings.GetAsync<string>(SettingKeys.Claude.SummarySystemPrompt, ct) ?? string.Empty;
            var template = (await _settings.GetAsync<string>(SettingKeys.Claude.SummaryTemplate, ct) ?? string.Empty).Trim();
            var systemPrompt = BuildSummarySystemPrompt(instructions, template);
            var maxContextChars = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.MaxContextChars, ct), 1000, 200_000);
            var userText = await BuildTicketContextAsync(
                ticketId, "Here is the full content of the ticket to summarise.", maxContextChars, ct);
            // Summaries are text-only by design: images are never sent.
            return (systemPrompt, userText, (IReadOnlyList<ClaudeImageInput>)Array.Empty<ClaudeImageInput>());
        }, ct);

    public Task<ClaudeProposalResult> GenerateStatusAsync(
        Guid ticketId,
        Guid userId,
        CancellationToken ct) =>
        RunAsync(ClaudeFeatures.Status, ticketId, userId, async () =>
        {
            var instructions = await _settings.GetAsync<string>(SettingKeys.Claude.StatusSystemPrompt, ct) ?? string.Empty;
            var template = (await _settings.GetAsync<string>(SettingKeys.Claude.StatusTemplate, ct) ?? string.Empty).Trim();
            var systemPrompt = BuildSummarySystemPrompt(instructions, template);
            var maxContextChars = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.StatusMaxContextChars, ct), 1000, 200_000);
            var userText = await BuildStatusContextAsync(ticketId, maxContextChars, ct);
            // Text only, like the summary: images are never sent.
            return (systemPrompt, userText, (IReadOnlyList<ClaudeImageInput>)Array.Empty<ClaudeImageInput>());
        }, ct);

    /// The admin-editable instructions followed by the admin-editable template.
    /// Both are trusted (admin-only settings); the ticket stays in the user turn.
    internal static string BuildSummarySystemPrompt(string instructions, string template) =>
        instructions.TrimEnd()
        + "\n\nTemplate (reproduce its headings and formatting exactly; fill in the bullets):\n<template>\n"
        + template + "\n</template>";

    /// Shared pipeline for every in-ticket assist call: guards (kill-switch,
    /// key, ZDR, budget — no API call, no cost), prompt assembly via
    /// <paramref name="buildPrompt"/>, the API call, and usage logging tagged
    /// with <paramref name="feature"/>.
    private async Task<ClaudeProposalResult> RunAsync(
        string feature,
        Guid ticketId,
        Guid userId,
        Func<Task<(string SystemPrompt, string UserText, IReadOnlyList<ClaudeImageInput> Images)>> buildPrompt,
        CancellationToken ct)
    {
        var monthStartUtc = MonthStartUtc(DateTime.UtcNow);

        // ---- Guards (no API call, no cost) -----------------------------
        var enabled = await _settings.GetAsync<bool>(SettingKeys.Claude.Enabled, ct);
        if (!enabled)
            return await BlockedAsync(feature, userId, ticketId, "disabled", ClaudeProposalOutcome.Disabled,
                "The Claude AI assistant is turned off.", monthStartUtc, ct);

        var hasKey = await _secrets.HasAsync(ProtectedSecretKeys.ClaudeApiKey, ct);
        if (!hasKey)
            return await BlockedAsync(feature, userId, ticketId, "not_configured", ClaudeProposalOutcome.NotConfigured,
                "The Claude AI assistant has no API key configured.", monthStartUtc, ct);

        var zdrConfirmed = await _settings.GetAsync<bool>(SettingKeys.Claude.ZeroDataRetentionConfirmed, ct);
        if (!zdrConfirmed)
            return await BlockedAsync(feature, userId, ticketId, "zdr_not_confirmed", ClaudeProposalOutcome.ZdrNotConfirmed,
                "Zero data retention has not been confirmed for the Claude organisation.", monthStartUtc, ct);

        var effectiveBudgetCents = await ResolveBudgetCentsAsync(userId, ct);
        var budgetMicro = (long)effectiveBudgetCents * 10_000L;
        if (effectiveBudgetCents <= 0)
            return await BlockedAsync(feature, userId, ticketId, "no_budget", ClaudeProposalOutcome.NoBudget,
                "You have no Claude AI budget assigned.", monthStartUtc, ct);

        var spendMicro = await _usage.GetMonthSpendMicroEurAsync(userId, monthStartUtc, ct);
        if (spendMicro >= budgetMicro)
        {
            await _usage.LogAsync(new ClaudeUsageEntry(userId, ticketId, "", 0, 0, 0, 0, "blocked", "budget_exceeded", null, feature), ct);
            return new ClaudeProposalResult(ClaudeProposalOutcome.BudgetExceeded, null, null,
                "Your monthly Claude AI budget is exhausted.", 0, 0, 0, 0, spendMicro, budgetMicro);
        }

        // ---- Build the scoped prompt -----------------------------------
        var (systemPrompt, userText, images) = await buildPrompt();
        var auditEventType = feature switch
        {
            ClaudeFeatures.Summary => ClaudeEventTypes.SummaryCall,
            ClaudeFeatures.Status => ClaudeEventTypes.StatusCall,
            _ => ClaudeEventTypes.ProposalCall,
        };
        // Summary and status fill a template; the proposal is free-form.
        var fillsTemplate = feature is ClaudeFeatures.Summary or ClaudeFeatures.Status;

        var (model, effort) = await ResolveModelAsync(feature, ct);

        // ---- Call + log -------------------------------------------------
        try
        {
            var result = await _api.CreateProposalAsync(systemPrompt, userText, images, auditEventType, model, effort, ct);

            // Priced by the model that answered; the shared settings are the
            // fallback for a model missing from the price list.
            var price = ClaudeModelPrices.Resolve(
                await _settings.GetAsync<string>(SettingKeys.Claude.ModelPrices, ct), result.Model);
            var inputPriceCents = price?.InputCentsPerMTok ?? await _settings.GetAsync<int>(SettingKeys.Claude.InputPriceCentsPerMTok, ct);
            var outputPriceCents = price?.OutputCentsPerMTok ?? await _settings.GetAsync<int>(SettingKeys.Claude.OutputPriceCentsPerMTok, ct);
            var costMicro = ((long)result.InputTokens * inputPriceCents + (long)result.OutputTokens * outputPriceCents) / 100L;

            var refused = string.Equals(result.StopReason, "refusal", StringComparison.OrdinalIgnoreCase);

            await _usage.LogAsync(new ClaudeUsageEntry(
                userId, ticketId, result.Model, result.InputTokens, result.OutputTokens,
                costMicro, images.Count, "ok", refused ? "refusal" : null, result.RequestId, feature), ct);

            var newSpend = spendMicro + costMicro;

            if (refused || string.IsNullOrWhiteSpace(result.Text))
            {
                return new ClaudeProposalResult(
                    ClaudeProposalOutcome.Refused, null, null,
                    feature switch
                    {
                        ClaudeFeatures.Summary => "The assistant declined to produce a summary for this ticket's content.",
                        ClaudeFeatures.Status => "The assistant declined to produce a status update for this ticket's content.",
                        _ => "The assistant declined to produce a proposal for this ticket's content.",
                    },
                    result.InputTokens, result.OutputTokens, costMicro, images.Count, newSpend, budgetMicro);
            }

            var text = fillsTemplate ? StripCodeFence(result.Text) : result.Text;
            return new ClaudeProposalResult(
                ClaudeProposalOutcome.Ok,
                text,
                MarkdownToHtml(text),
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
                ex.UpstreamErrorCode ?? "api_error", null, feature), ct);
            throw;
        }
    }

    /// Model + effort per feature: the summary and the status update have
    /// their own (speed over depth); empty falls back to the proposal's.
    private async Task<(string Model, string Effort)> ResolveModelAsync(string feature, CancellationToken ct)
    {
        var baseModel = (await _settings.GetAsync<string>(SettingKeys.Claude.Model, ct) ?? string.Empty).Trim();
        if (baseModel.Length == 0) baseModel = "claude-sonnet-5-5";
        var baseEffort = (await _settings.GetAsync<string>(SettingKeys.Claude.Effort, ct) ?? "off").Trim();

        var (modelKey, effortKey) = feature switch
        {
            ClaudeFeatures.Summary => (SettingKeys.Claude.SummaryModel, SettingKeys.Claude.SummaryEffort),
            ClaudeFeatures.Status => (SettingKeys.Claude.StatusModel, SettingKeys.Claude.StatusEffort),
            _ => (null, null),
        };
        if (modelKey is null || effortKey is null) return (baseModel, baseEffort);

        var model = (await _settings.GetAsync<string>(modelKey, ct) ?? string.Empty).Trim();
        var effort = (await _settings.GetAsync<string>(effortKey, ct) ?? string.Empty).Trim();
        return (model.Length > 0 ? model : baseModel, effort.Length > 0 ? effort : baseEffort);
    }

    /// Defensive: if the model wraps the filled template in a ``` fence
    /// despite the instructions, unwrap it so the template's own formatting
    /// renders instead of a code block.
    internal static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (t.Length < 6 || !t.StartsWith("```", StringComparison.Ordinal) || !t.EndsWith("```", StringComparison.Ordinal))
            return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0 || firstNewline > t.Length - 4) return t;
        return t[(firstNewline + 1)..^3].Trim();
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
        string feature, Guid userId, Guid ticketId, string code, ClaudeProposalOutcome outcome,
        string message, DateTime monthStartUtc, CancellationToken ct)
    {
        await _usage.LogAsync(new ClaudeUsageEntry(userId, ticketId, "", 0, 0, 0, 0, "blocked", code, null, feature), ct);
        var spend = await _usage.GetMonthSpendMicroEurAsync(userId, monthStartUtc, ct);
        return new ClaudeProposalResult(outcome, null, null, message, 0, 0, 0, 0, spend, 0);
    }

    // ---- Ticket context ------------------------------------------------

    private async Task<string> BuildTicketContextAsync(Guid ticketId, string intro, int maxChars, CancellationToken ct)
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
        sb.Append(intro).Append("\n\n");
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

    /// Context for the status update. Unlike the proposal/summary context it
    /// carries the ticket's current state, a time and author per event and
    /// the field changes, so the model can say what the ticket waits on and
    /// since when. Earlier AI status notes are left out so a brief never
    /// summarises a previous brief. Over budget, the newest events are kept
    /// (<see cref="ClaudeStatusContext.FitTimeline"/>).
    private async Task<string> BuildStatusContextAsync(Guid ticketId, int maxChars, CancellationToken ct)
    {
        var tz = await HealthTimeFormat.ResolveDisplayZoneAsync(_settings, ct);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        const string headSql = """
            SELECT t.number           AS Number,
                   t.subject          AS Subject,
                   COALESCE(b.body_text, '') AS Body,
                   t.created_utc      AS CreatedUtc,
                   s.name             AS StatusName,
                   s.state_category   AS StatusCategory,
                   p.name             AS PriorityName,
                   q.name             AS QueueName,
                   COALESCE(NULLIF(u.display_name, ''), u.email::text) AS AssigneeName,
                   NULLIF(TRIM(c.first_name || ' ' || c.last_name), '') AS RequesterName,
                   co.name            AS CompanyName,
                   t.pending_till_utc AS PendingTillUtc,
                   t.is_callback      AS IsCallback,
                   t.is_research      AS IsResearch,
                   t.is_project       AS IsProject
              FROM tickets t
              LEFT JOIN ticket_bodies b ON b.ticket_id = t.id
              LEFT JOIN statuses s      ON s.id = t.status_id
              LEFT JOIN priorities p    ON p.id = t.priority_id
              LEFT JOIN queues q        ON q.id = t.queue_id
              LEFT JOIN users u         ON u.id = t.assignee_user_id
              LEFT JOIN contacts c      ON c.id = t.requester_contact_id
              LEFT JOIN companies co    ON co.id = t.company_id
             WHERE t.id = @id
            """;
        var head = await conn.QuerySingleOrDefaultAsync<StatusHeadRow>(
            new CommandDefinition(headSql, new { id = ticketId }, cancellationToken: ct));

        const string checklistSql = """
            SELECT cl.name AS ChecklistName, i.title AS Title, i.is_required AS IsRequired
              FROM ticket_checklist_items i
              JOIN ticket_checklists cl ON cl.id = i.checklist_id
             WHERE cl.ticket_id = @id AND i.state = 'open'
             ORDER BY cl.sort_order, cl.attached_utc, i.sort_order
             LIMIT 50
            """;
        var openItems = (await conn.QueryAsync<StatusChecklistRow>(
            new CommandDefinition(checklistSql, new { id = ticketId }, cancellationToken: ct))).ToList();

        // Newest first so a very long ticket keeps its recent history, then
        // reversed into reading order.
        const string eventsSql = """
            SELECT e.event_type        AS EventType,
                   e.body_text         AS BodyText,
                   e.is_internal       AS IsInternal,
                   e.metadata::text    AS MetadataJson,
                   e.created_utc       AS CreatedUtc,
                   e.author_user_id    AS AuthorUserId,
                   e.author_contact_id AS AuthorContactId,
                   COALESCE(NULLIF(u.display_name, ''), u.email::text) AS UserName,
                   NULLIF(TRIM(c.first_name || ' ' || c.last_name), '') AS ContactName
              FROM ticket_events e
              LEFT JOIN users u    ON u.id = e.author_user_id
              LEFT JOIN contacts c ON c.id = e.author_contact_id
             WHERE e.ticket_id = @id
               AND COALESCE(e.metadata->>@aiKey, '') <> @aiValue
               AND ((e.body_text IS NOT NULL AND e.body_text <> '')
                    OR e.event_type = ANY(@changeTypes))
             ORDER BY e.created_utc DESC, e.id DESC
             LIMIT 500
            """;
        var events = (await conn.QueryAsync<StatusEventRow>(new CommandDefinition(eventsSql, new
        {
            id = ticketId,
            aiKey = ClaudeStatusContext.AiGeneratedKey,
            aiValue = ClaudeStatusContext.AiGeneratedStatus,
            changeTypes = ClaudeStatusContext.ChangeEventTypes,
        }, cancellationToken: ct))).ToList();
        events.Reverse();

        var sb = new StringBuilder();
        sb.Append("Here is the ticket to write a status update for. All times are ")
          .Append(tz.Id).Append(" local time.\n");
        sb.Append("Now: ").Append(ClaudeStatusContext.FormatLocal(DateTime.UtcNow, tz)).Append("\n\n");
        if (head is not null)
        {
            sb.Append("Ticket #").Append(head.Number).Append('\n');
            sb.Append("Subject: ").Append(head.Subject).Append('\n');
            sb.Append("Created: ").Append(ClaudeStatusContext.FormatLocal(head.CreatedUtc, tz)).Append('\n');
            sb.Append("Current status: ").Append(head.StatusName ?? "unknown");
            if (!string.IsNullOrEmpty(head.StatusCategory)) sb.Append(" (").Append(head.StatusCategory).Append(')');
            sb.Append('\n');
            if (head.PendingTillUtc.HasValue)
                sb.Append("Pending till: ").Append(ClaudeStatusContext.FormatLocal(head.PendingTillUtc.Value, tz)).Append('\n');
            sb.Append("Priority: ").Append(head.PriorityName ?? "unknown").Append('\n');
            sb.Append("Queue: ").Append(head.QueueName ?? "unknown").Append('\n');
            sb.Append("Assigned to: ").Append(head.AssigneeName ?? "nobody").Append('\n');
            sb.Append("Requester: ").Append(head.RequesterName ?? "unknown");
            if (!string.IsNullOrEmpty(head.CompanyName)) sb.Append(" (").Append(head.CompanyName).Append(')');
            sb.Append('\n');
            var flags = new List<string>();
            if (head.IsCallback) flags.Add("call-back requested");
            if (head.IsResearch) flags.Add("under research");
            if (head.IsProject) flags.Add("project ticket");
            if (flags.Count > 0) sb.Append("Flags: ").Append(string.Join(", ", flags)).Append('\n');
        }
        if (openItems.Count > 0)
        {
            sb.Append("Open checklist items:\n");
            foreach (var item in openItems)
                sb.Append("- ").Append(item.ChecklistName).Append(": ").Append(item.Title)
                  .Append(item.IsRequired ? "" : " (optional)").Append('\n');
        }
        if (head is not null)
            sb.Append("\nDescription:\n").Append(Clamp(head.Body, 8000)).Append("\n\n");

        var lines = events
            .Select(e => ClaudeStatusContext.FormatEvent(e, tz))
            .OfType<string>()
            .ToList();

        var header = sb.ToString();
        if (lines.Count == 0) return Clamp(header, maxChars);

        const string timelineTitle = "Timeline (oldest first):\n";
        var timeline = ClaudeStatusContext.FitTimeline(lines, maxChars - header.Length - timelineTitle.Length);
        return Clamp(header + timelineTitle + timeline, maxChars);
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

    /// Delegates to the shared <see cref="ClaudeMarkdown"/> renderer; kept as a
    /// thin wrapper so the existing unit tests keep their entry point. Internal
    /// because the escaping is security-sensitive.
    internal static string MarkdownToHtml(string text) => ClaudeMarkdown.MarkdownToHtml(text);

    private sealed class TicketHeadRow
    {
        public long Number { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }

    private sealed class StatusHeadRow
    {
        public long Number { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public string? StatusName { get; set; }
        public string? StatusCategory { get; set; }
        public string? PriorityName { get; set; }
        public string? QueueName { get; set; }
        public string? AssigneeName { get; set; }
        public string? RequesterName { get; set; }
        public string? CompanyName { get; set; }
        public DateTime? PendingTillUtc { get; set; }
        public bool IsCallback { get; set; }
        public bool IsResearch { get; set; }
        public bool IsProject { get; set; }
    }

    private sealed class StatusChecklistRow
    {
        public string ChecklistName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsRequired { get; set; }
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
