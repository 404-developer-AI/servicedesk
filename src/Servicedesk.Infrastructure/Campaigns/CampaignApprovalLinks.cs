using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Servicedesk.Infrastructure.Security;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Campaigns;

/// The answer link of one recipient for one round: the URL to embed, or why
/// the ticket cannot get one.
public sealed record CampaignLinkPlan(string? Url, string? SkipReason);

/// v0.1.67 (phase 2) — the customer's answer link
/// (<c>{{campaign.approvalLink}}</c>). One link per recipient per campaign;
/// a follow-up round embeds the same link again (the raw token is recovered
/// from its DataProtection ciphertext, never stored in plain text). The link
/// is valid until the proposed date, capped by Campaigns.ApprovalLinkMaxDays;
/// every round that embeds it moves the expiry to the current proposed date.
public interface ICampaignApprovalLinks
{
    /// Plans the link of each recipient. With <paramref name="mint"/> false
    /// (dry run, preview) nothing is written and the URL is a preview URL.
    Task<IReadOnlyDictionary<Guid, CampaignLinkPlan>> PlanAsync(
        Guid campaignId, IReadOnlyDictionary<Guid, DateTime?> proposedByTicket, DateTime nowUtc, bool mint, CancellationToken ct);
}

public sealed class CampaignApprovalLinks : ICampaignApprovalLinks
{
    public const string DataProtectionPurpose = "Servicedesk.Campaigns.AnswerToken.v1";
    public const string PublicPath = "/campaign-answer/";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;
    private readonly LinkTokenCodec _codec;

    public CampaignApprovalLinks(NpgsqlDataSource dataSource, ISettingsService settings, IDataProtectionProvider dataProtection)
    {
        _dataSource = dataSource;
        _settings = settings;
        _codec = new LinkTokenCodec(dataProtection, DataProtectionPurpose);
    }

    public async Task<IReadOnlyDictionary<Guid, CampaignLinkPlan>> PlanAsync(
        Guid campaignId, IReadOnlyDictionary<Guid, DateTime?> proposedByTicket, DateTime nowUtc, bool mint, CancellationToken ct)
    {
        var result = new Dictionary<Guid, CampaignLinkPlan>();
        if (proposedByTicket.Count == 0) return result;

        var baseUrl = (await _settings.GetOrAsync<string?>(SettingKeys.App.PublicBaseUrl, null, ct))?.Trim().TrimEnd('/');
        var maxDays = Math.Clamp(await _settings.GetOrAsync<int>(SettingKeys.Campaigns.ApprovalLinkMaxDays, 60, ct), 1, 365);
        var cap = nowUtc.AddDays(maxDays);

        var due = new Dictionary<Guid, DateTime>();
        foreach (var (ticketId, proposed) in proposedByTicket)
        {
            if (string.IsNullOrEmpty(baseUrl))
                result[ticketId] = new CampaignLinkPlan(null, CampaignSkipReasons.AnswerLinkUnavailable);
            else if (proposed is { } p && p <= nowUtc)
                result[ticketId] = new CampaignLinkPlan(null, CampaignSkipReasons.ProposedDatePassed);
            else
                due[ticketId] = proposed is { } q && q < cap ? q : cap;
        }
        if (due.Count == 0) return result;

        if (!mint)
        {
            foreach (var id in due.Keys) result[id] = new CampaignLinkPlan(baseUrl + PublicPath + "preview", null);
            return result;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var existing = (await conn.QueryAsync<StoredToken>(new CommandDefinition("""
            SELECT ticket_id AS TicketId, approval_token_cipher AS Cipher
            FROM campaign_tickets
            WHERE campaign_id = @campaignId AND ticket_id = ANY(@ids)
            FOR UPDATE
            """, new { campaignId, ids = due.Keys.ToArray() }, tx, cancellationToken: ct)))
            .ToDictionary(r => r.TicketId, r => r.Cipher);

        foreach (var (ticketId, expires) in due)
        {
            if (!existing.ContainsKey(ticketId)) continue; // removed meanwhile
            string? raw = existing[ticketId] is { } cipher ? _codec.Reveal(cipher) : null;
            if (raw is null)
            {
                var (newRaw, hash, newCipher) = _codec.Mint();
                raw = newRaw;
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE campaign_tickets
                    SET approval_token_hash = @hash, approval_token_cipher = @cipher, approval_expires_utc = @expires
                    WHERE campaign_id = @campaignId AND ticket_id = @ticketId
                    """, new { hash, cipher = newCipher, expires, campaignId, ticketId }, tx, cancellationToken: ct));
            }
            else
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE campaign_tickets SET approval_expires_utc = @expires
                    WHERE campaign_id = @campaignId AND ticket_id = @ticketId
                    """, new { expires, campaignId, ticketId }, tx, cancellationToken: ct));
            }
            result[ticketId] = new CampaignLinkPlan(baseUrl + PublicPath + raw, null);
        }
        await tx.CommitAsync(ct);
        return result;
    }

    private sealed class StoredToken
    {
        public Guid TicketId { get; set; }
        public byte[]? Cipher { get; set; }
    }
}
