using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Security;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Triggers;

namespace Servicedesk.Infrastructure.Campaigns;

/// What the public answer page shows. A whitelist: the campaign name, the
/// ticket reference, the proposed moment, the campaign's public "Another
/// moment" notice and the customer's own answer — never internal ticket data.
/// <see cref="PreferredMinDate"/>/<see cref="PreferredMaxDate"/> bound the
/// date picker (server-local calendar dates, "yyyy-MM-dd").
public sealed record CampaignAnswerView(
    string CampaignName,
    long TicketNumber,
    string? ProposedText,
    DateTime ExpiresUtc,
    string? ExpiresText,
    string? Answer,
    string? Note,
    DateTime? AnsweredUtc,
    string? AnsweredText,
    string OtherTimeNotice,
    string PreferredMinDate,
    string PreferredMaxDate,
    string? PreferredDate,
    int? PreferredMinute,
    string? PreferredText);

public enum CampaignAnswerOutcome { Recorded, NotFound, Expired, Invalid }

public sealed record CampaignAnswerResult(CampaignAnswerOutcome Outcome, CampaignAnswerView? View = null, string? Error = null);

public interface ICampaignAnswerService
{
    /// The page for a raw token from the URL; Expired when the link ran out.
    Task<CampaignAnswerResult> GetAsync(string rawToken, CancellationToken ct);

    /// Records (or changes) the customer's answer while the link is valid.
    /// With "Another moment" a preferred date is required (tomorrow up to a
    /// year ahead, server-local) and a time of day optional.
    Task<CampaignAnswerResult> SubmitAsync(string rawToken, string? choice, string? note,
        DateTime? preferredDate, int? preferredMinute, string? clientIp, string? userAgent, CancellationToken ct);
}

/// v0.1.67 (phase 2) — the customer's answer through the campaign link.
/// The token hash is the only key; the link is valid until its expiry
/// (the proposed date, capped) and the customer may change the answer until
/// then. An answer writes the decision on the campaign (source Customer +
/// optional note) in one guarded UPDATE, adds a customer-visible
/// <c>CampaignAnswer</c> event to the ticket (author = the requester) and
/// runs the triggers like any customer article — so "customer answered →
/// reopen" rules apply. The mail reply stays the main path; this is extra.
public sealed class CampaignAnswerService : ICampaignAnswerService
{
    public const int MaxNoteLength = 2000;
    /// The furthest preferred date a customer can pick (days from today).
    public const int MaxPreferredDaysAhead = 365;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITicketRepository _tickets;
    private readonly ITriggerService _triggers;
    private readonly ITicketListNotifier _ticketList;
    private readonly ICampaignNotifier _notifier;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _clock;

    public CampaignAnswerService(
        NpgsqlDataSource dataSource, ITicketRepository tickets, ITriggerService triggers,
        ITicketListNotifier ticketList, ICampaignNotifier notifier, ISettingsService settings,
        IAuditLogger audit, TimeProvider clock)
    {
        _dataSource = dataSource;
        _tickets = tickets;
        _triggers = triggers;
        _ticketList = ticketList;
        _notifier = notifier;
        _settings = settings;
        _audit = audit;
        _clock = clock;
    }

    public async Task<CampaignAnswerResult> GetAsync(string rawToken, CancellationToken ct)
    {
        var hash = LinkTokenCodec.HashForLookup(rawToken);
        if (hash is null) return new CampaignAnswerResult(CampaignAnswerOutcome.NotFound);

        var row = await LoadAsync(hash, ct);
        if (row is null) return new CampaignAnswerResult(CampaignAnswerOutcome.NotFound);
        if (!IsOpen(row)) return new CampaignAnswerResult(CampaignAnswerOutcome.Expired);
        return new CampaignAnswerResult(CampaignAnswerOutcome.Recorded, await ViewAsync(row, ct));
    }

    public async Task<CampaignAnswerResult> SubmitAsync(string rawToken, string? choice, string? note,
        DateTime? preferredDate, int? preferredMinute, string? clientIp, string? userAgent, CancellationToken ct)
    {
        var hash = LinkTokenCodec.HashForLookup(rawToken);
        if (hash is null) return new CampaignAnswerResult(CampaignAnswerOutcome.NotFound);
        if (!CampaignDecisions.IsValid(choice))
            return new CampaignAnswerResult(CampaignAnswerOutcome.Invalid, Error: "Choose approve, another moment or decline.");
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote is { Length: > MaxNoteLength })
            return new CampaignAnswerResult(CampaignAnswerOutcome.Invalid, Error: $"The note is at most {MaxNoteLength} characters.");

        var format = await CampaignFormat.LoadAsync(_settings, ct);
        var (minDate, maxDate) = PreferredBounds(format);
        DateTime? date = null;
        int? minute = null;
        if (choice == CampaignDecisions.OtherTime)
        {
            if (preferredDate is not { } d || d.Date < minDate || d.Date > maxDate)
                return new CampaignAnswerResult(CampaignAnswerOutcome.Invalid,
                    Error: $"Pick a date between {minDate:dd/MM/yyyy} and {maxDate:dd/MM/yyyy}.");
            if (preferredMinute is { } pm && pm is < 0 or > 1439)
                return new CampaignAnswerResult(CampaignAnswerOutcome.Invalid, Error: "Pick a valid time.");
            date = DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified);
            minute = preferredMinute;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var args = new DynamicParameters(new { hash, choice, note = cleanNote, now, minute });
        args.Add("date", date, System.Data.DbType.Date);
        // One guarded statement: only a valid, unexpired link on a live
        // ticket of a non-archived campaign records anything.
        var updated = await conn.QueryFirstOrDefaultAsync<AnswerRow>(new CommandDefinition("""
            WITH prev AS (
                SELECT ct.campaign_id, ct.ticket_id, ct.decision, ct.decision_source
                FROM campaign_tickets ct
                WHERE ct.approval_token_hash = @hash
                FOR UPDATE
            )
            UPDATE campaign_tickets ct SET
                decision            = @choice,
                decision_source     = 'Customer',
                decision_note       = @note,
                decision_preferred_date   = @date,
                decision_preferred_minute = @minute,
                decision_by_user_id = NULL,
                decision_utc        = @now
            FROM prev, campaigns c, tickets t
            WHERE ct.campaign_id = prev.campaign_id AND ct.ticket_id = prev.ticket_id
              AND c.id = ct.campaign_id AND c.state <> 'Archived'
              AND t.id = ct.ticket_id AND t.is_deleted = FALSE AND t.merged_into_ticket_id IS NULL
              AND ct.approval_expires_utc > @now
            RETURNING ct.campaign_id AS CampaignId, ct.ticket_id AS TicketId, c.name AS CampaignName,
                      t.number AS TicketNumber, t.requester_contact_id AS RequesterContactId,
                      ct.proposed_utc AS ProposedUtc, c.default_proposed_utc AS DefaultProposedUtc,
                      ct.approval_expires_utc AS ExpiresUtc, c.state AS CampaignState,
                      FALSE AS TicketGone, ct.decision AS Answer, ct.decision_note AS Note,
                      ct.decision_utc AS AnsweredUtc, ct.decision_preferred_date AS PreferredDate,
                      ct.decision_preferred_minute AS PreferredMinute, c.other_time_notice AS OtherTimeNotice,
                      COALESCE(prev.decision_source = 'Customer', FALSE) AS Changed
            """, args, cancellationToken: ct));

        if (updated is null)
        {
            var row = await LoadAsync(hash, ct);
            return new CampaignAnswerResult(row is null ? CampaignAnswerOutcome.NotFound : CampaignAnswerOutcome.Expired);
        }

        var proposedText = CampaignRenderer.ProposedText(updated.ProposedUtc ?? updated.DefaultProposedUtc, format);
        var preferredText = date is { } pd ? CampaignRenderer.PreferredText(pd, minute, format) : null;
        var evt = await _tickets.AddEventAsync(updated.TicketId, new NewTicketEvent(
            EventType: "CampaignAnswer",
            BodyText: cleanNote,
            BodyHtml: null,
            IsInternal: false,
            AuthorUserId: null,
            AuthorContactId: updated.RequesterContactId,
            MetadataJson: JsonSerializer.Serialize(new
            {
                campaignId = updated.CampaignId,
                campaignName = updated.CampaignName,
                choice,
                proposedText,
                preferredText,
                changed = updated.Changed,
            })), ct);

        await _audit.LogAsync(new AuditEvent(
            EventType: "campaign.customer_answer",
            Actor: "customer",
            ActorRole: "anon",
            Target: updated.CampaignId.ToString(),
            ClientIp: clientIp,
            UserAgent: userAgent,
            Payload: new { ticketId = updated.TicketId, choice, hasNote = cleanNote is not null, preferredDate = date, changed = updated.Changed }), ct);

        if (evt is not null)
        {
            // A customer touched the ticket: same trigger pass as a customer
            // article, so admins can route on `article.type = CampaignAnswer`.
            await _triggers.EvaluateAsync(
                ticketId: updated.TicketId,
                ticketEventId: evt.Id,
                activatorKind: TriggerActivatorKind.Action,
                changeSet: TriggerChangeSet.ArticleOnly(),
                ct: ct);
        }
        await _ticketList.NotifyUpdatedAsync(updated.TicketId, ct);
        await _notifier.CampaignChangedAsync(updated.CampaignId, ct);

        return new CampaignAnswerResult(CampaignAnswerOutcome.Recorded, await ViewAsync(updated, ct));
    }

    private bool IsOpen(AnswerRow r) =>
        !r.TicketGone && r.CampaignState != CampaignStates.Archived
        && r.ExpiresUtc is { } exp && exp > _clock.GetUtcNow().UtcDateTime;

    private async Task<CampaignAnswerView> ViewAsync(AnswerRow r, CancellationToken ct)
    {
        var format = await CampaignFormat.LoadAsync(_settings, ct);
        var (minDate, maxDate) = PreferredBounds(format);
        return new CampaignAnswerView(
            r.CampaignName, r.TicketNumber,
            CampaignRenderer.ProposedText(r.ProposedUtc ?? r.DefaultProposedUtc, format),
            r.ExpiresUtc ?? DateTime.MinValue,
            CampaignRenderer.ProposedText(r.ExpiresUtc, format),
            r.Answer, r.Note, r.AnsweredUtc,
            CampaignRenderer.ProposedText(r.AnsweredUtc, format),
            r.OtherTimeNotice,
            minDate.ToString("yyyy-MM-dd"), maxDate.ToString("yyyy-MM-dd"),
            r.PreferredDate?.ToString("yyyy-MM-dd"), r.PreferredMinute,
            r.PreferredDate is { } pd ? CampaignRenderer.PreferredText(pd, r.PreferredMinute, format) : null);
    }

    /// Tomorrow up to a year ahead, as server-local calendar dates.
    private (DateTime Min, DateTime Max) PreferredBounds(CampaignFormat format)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, format.Zone).Date;
        return (today.AddDays(1), today.AddDays(MaxPreferredDaysAhead));
    }

    private async Task<AnswerRow?> LoadAsync(byte[] hash, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AnswerRow>(new CommandDefinition("""
            SELECT ct.campaign_id AS CampaignId, ct.ticket_id AS TicketId, c.name AS CampaignName,
                   t.number AS TicketNumber, t.requester_contact_id AS RequesterContactId,
                   ct.proposed_utc AS ProposedUtc, c.default_proposed_utc AS DefaultProposedUtc,
                   ct.approval_expires_utc AS ExpiresUtc, c.state AS CampaignState,
                   (t.is_deleted OR t.merged_into_ticket_id IS NOT NULL) AS TicketGone,
                   CASE WHEN ct.decision_source = 'Customer' THEN ct.decision END AS Answer,
                   CASE WHEN ct.decision_source = 'Customer' THEN ct.decision_note END AS Note,
                   CASE WHEN ct.decision_source = 'Customer' THEN ct.decision_utc END AS AnsweredUtc,
                   CASE WHEN ct.decision_source = 'Customer' THEN ct.decision_preferred_date END AS PreferredDate,
                   CASE WHEN ct.decision_source = 'Customer' THEN ct.decision_preferred_minute END AS PreferredMinute,
                   c.other_time_notice AS OtherTimeNotice,
                   FALSE AS Changed
            FROM campaign_tickets ct
            JOIN campaigns c ON c.id = ct.campaign_id
            JOIN tickets t ON t.id = ct.ticket_id
            WHERE ct.approval_token_hash = @hash
            """, new { hash }, cancellationToken: ct));
    }

    private sealed class AnswerRow
    {
        public Guid CampaignId { get; set; }
        public Guid TicketId { get; set; }
        public string CampaignName { get; set; } = string.Empty;
        public long TicketNumber { get; set; }
        public Guid RequesterContactId { get; set; }
        public DateTime? ProposedUtc { get; set; }
        public DateTime? DefaultProposedUtc { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public string CampaignState { get; set; } = string.Empty;
        public bool TicketGone { get; set; }
        public string? Answer { get; set; }
        public string? Note { get; set; }
        public DateTime? AnsweredUtc { get; set; }
        public DateTime? PreferredDate { get; set; }
        public int? PreferredMinute { get; set; }
        public string OtherTimeNotice { get; set; } = string.Empty;
        public bool Changed { get; set; }
    }
}
