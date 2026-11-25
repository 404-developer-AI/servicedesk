using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Campaigns;

public interface ICampaignSendService
{
    /// Sends at most one due outbox row. True when a row was handled.
    Task<bool> SendNextAsync(CancellationToken ct);

    /// Stale-claim reconciliation + round completion (project event, audit).
    Task HousekeepAsync(CancellationToken ct);
}

/// v0.1.67 — sends one campaign mail through the normal single-ticket mail
/// path. Before the send every rule is checked again as the sender is
/// *now* (active, Campaigns flag, queue access) and as the ticket is *now*
/// (project gate, closed, merged, requester address unchanged); a failed
/// check skips the ticket with a reason, never forces it. The mail itself
/// goes through <see cref="IOutboundMailService"/> — threading, signature,
/// MailSent event, SLA and triggers are the ones every agent mail gets.
/// The optional "set Pending till" then runs through the shared bulk
/// mutation path (<see cref="ITicketBulkActionService"/>, origin Campaign).
public sealed class CampaignSendService : ICampaignSendService
{
    internal static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(10);

    private readonly ICampaignOutboxRepository _outbox;
    private readonly IOutboundMailService _mail;
    private readonly IQueueAccessService _queueAccess;
    private readonly ITicketBulkActionService _bulk;
    private readonly ITicketRepository _tickets;
    private readonly ITicketListNotifier _ticketList;
    private readonly ICampaignNotifier _notifier;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<CampaignSendService> _logger;

    public CampaignSendService(
        ICampaignOutboxRepository outbox, IOutboundMailService mail, IQueueAccessService queueAccess,
        ITicketBulkActionService bulk, ITicketRepository tickets, ITicketListNotifier ticketList,
        ICampaignNotifier notifier, ISettingsService settings, IAuditLogger audit, TimeProvider clock,
        ILogger<CampaignSendService> logger)
    {
        _outbox = outbox;
        _mail = mail;
        _queueAccess = queueAccess;
        _bulk = bulk;
        _tickets = tickets;
        _ticketList = ticketList;
        _notifier = notifier;
        _settings = settings;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<bool> SendNextAsync(CancellationToken ct)
    {
        if (!await _settings.GetOrAsync<bool>(SettingKeys.Campaigns.Enabled, true, ct)) return false;

        var claimed = await _outbox.ClaimDueAsync(1, ct);
        if (claimed.Count == 0) return false;
        var msg = claimed[0];
        try
        {
            await SendOneAsync(msg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the row stays Sending; reconciliation settles it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Campaign message {MessageId} failed unexpectedly.", msg.Id);
            await _outbox.MarkFailedAsync(msg.Id, "Unexpected error: " + ex.Message, CancellationToken.None);
        }
        await _notifier.CampaignChangedAsync(msg.CampaignId, CancellationToken.None);
        return true;
    }

    private async Task SendOneAsync(ClaimedCampaignMessage msg, CancellationToken ct)
    {
        var c = await _outbox.GetSendContextAsync(msg.TicketId, msg.SentByUserId, ct);
        var skip = await RecheckAsync(msg, c, ct);
        if (skip is not null)
        {
            await _outbox.MarkSkippedAsync(msg.Id, skip, ct);
            return;
        }

        var role = c.SenderRole!;
        var request = new OutboundMailRequest(
            TicketId: msg.TicketId,
            AuthorUserId: msg.SentByUserId!.Value,
            Kind: OutboundMailKind.Reply,
            To: new[] { new GraphRecipient(c.RequesterEmail!.Trim(), c.RequesterName ?? c.RequesterEmail!.Trim()) },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: msg.RenderedSubject,
            BodyHtml: msg.RenderedBodyHtml,
            AuthorRole: role,
            ExtraEventMetadata: new Dictionary<string, string>
            {
                ["campaignId"] = msg.CampaignId.ToString(),
                ["campaignMessageId"] = msg.Id.ToString(),
                ["campaignName"] = msg.CampaignName,
            });

        OutboundMailResult result;
        try
        {
            result = await _mail.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await HandleSendErrorAsync(msg, ex, ct);
            return;
        }

        switch (result.Status)
        {
            case OutboundMailStatus.Sent:
                break;
            case OutboundMailStatus.NoMailboxConfigured:
                await _outbox.MarkSkippedAsync(msg.Id, CampaignSkipReasons.NoMailbox, ct);
                return;
            default:
                await _outbox.MarkFailedAsync(msg.Id, result.ErrorMessage ?? result.Status.ToString(), ct);
                return;
        }

        await _outbox.MarkSentAsync(msg.Id, result.Event!.Id, ct);
        await _audit.LogAsync(new AuditEvent(
            EventType: "ticket.mail.sent",
            Actor: c.SenderEmail ?? msg.SentByUserId.ToString()!,
            ActorRole: role,
            Target: msg.TicketId.ToString(),
            Payload: new
            {
                kind = "Campaign",
                campaignId = msg.CampaignId,
                campaignMessageId = msg.Id,
                toCount = 1,
                subject = msg.RenderedSubject,
            }), ct);
        await _ticketList.NotifyUpdatedAsync(msg.TicketId, ct);

        if (msg.PendingStatusId is { } statusId)
            await ApplyPendingAsync(msg, statusId, c, role, ct);
    }

    /// The rules as they are now. Null = send; otherwise the skip reason.
    private async Task<string?> RecheckAsync(ClaimedCampaignMessage msg, CampaignSendContext c, CancellationToken ct)
    {
        if (!c.TicketExists) return CampaignSkipReasons.Invalid;
        if (msg.SentByUserId is null || !c.SenderActive || !c.SenderFlag
            || c.SenderRole is not ("Agent" or "Admin"))
            return CampaignSkipReasons.SenderNotAllowed;
        if (!await _queueAccess.HasQueueAccessAsync(msg.SentByUserId.Value, c.SenderRole, c.QueueId, ct))
            return CampaignSkipReasons.NoAccess;

        var projectBlocked = await ProjectMailGuard.IsBlockedAsync(_settings, c.IsProject, ct);
        var reason = CampaignEligibility.Check(projectBlocked, c.IsMerged, c.StateCategory, c.RequesterEmail);
        if (reason is not null) return reason;

        // The body was rendered for the requester at queue time; a different
        // requester now would get a mail addressed to someone else.
        if (!string.Equals(c.RequesterEmail!.Trim(), msg.ToAddress, StringComparison.OrdinalIgnoreCase))
            return CampaignSkipReasons.RequesterChanged;
        return null;
    }

    private async Task HandleSendErrorAsync(ClaimedCampaignMessage msg, Exception ex, CancellationToken ct)
    {
        var transient = IsTransient(ex);
        var maxAttempts = Math.Clamp(await _settings.GetOrAsync<int>(SettingKeys.Campaigns.MaxAttempts, 5, ct), 1, 10);
        _logger.LogWarning(ex, "Campaign message {MessageId} (ticket {TicketId}) send failed, attempt {Attempt}, transient={Transient}.",
            msg.Id, msg.TicketId, msg.Attempts, transient);

        if (transient && msg.Attempts < maxAttempts)
        {
            var backoff = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, msg.Attempts)));
            await _outbox.MarkRetryAsync(msg.Id, ex.Message, _clock.GetUtcNow().UtcDateTime.Add(backoff), ct);
        }
        else
        {
            await _outbox.MarkFailedAsync(msg.Id, ex.Message, ct);
        }
    }

    /// Throttling (429), server errors (5xx) and network trouble are worth
    /// another try; any other Graph refusal is final.
    internal static bool IsTransient(Exception ex) => ex switch
    {
        Microsoft.Graph.Models.ODataErrors.ODataError odata =>
            odata.ResponseStatusCode is 429 or >= 500,
        HttpRequestException or TimeoutException or TaskCanceledException => true,
        _ => false,
    };

    private async Task ApplyPendingAsync(ClaimedCampaignMessage msg, Guid statusId, CampaignSendContext c, string role, CancellationToken ct)
    {
        var actor = new TicketMutationActor(
            msg.SentByUserId!.Value, role, c.SenderEmail ?? msg.SentByUserId.ToString()!, role, null, "campaign-outbox");
        string outcome;
        try
        {
            var r = await _bulk.ExecuteAsync(actor, new TicketBulkActionRequest(
                TicketIds: new[] { msg.TicketId },
                MessageHtml: null,
                MessageIsInternal: true,
                StatusId: statusId,
                QueueId: null,
                PriorityId: null,
                AssigneeUserId: null,
                UnassignAssignee: false,
                PendingTillUtc: msg.PendingTillUtc), ct, TicketBulkOrigin.Campaign);
            outcome = r.Succeeded > 0 ? "applied" : r.Skipped.FirstOrDefault()?.Reason ?? TicketBulkSkipReason.Failed;
        }
        catch (TicketBulkActionRejectedException ex)
        {
            outcome = ex.Code;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Campaign message {MessageId}: setting pending failed.", msg.Id);
            outcome = TicketBulkSkipReason.Failed;
        }
        await _outbox.MarkPendingResultAsync(msg.Id, outcome, ct);
    }

    public async Task HousekeepAsync(CancellationToken ct)
    {
        var reconciled = await _outbox.ReconcileStaleAsync(StaleClaimAge, ct);
        if (reconciled > 0)
            _logger.LogWarning("Campaign outbox: settled {Count} interrupted send(s).", reconciled);

        foreach (var round in await _outbox.CompleteSettledRoundsAsync(ct))
        {
            if (round.ProjectTicketId is { } projectId)
            {
                // Projects stay internal: the project only gets this note,
                // never a mail.
                var evt = await _tickets.AddEventAsync(projectId, new NewTicketEvent(
                    EventType: "CampaignSent",
                    BodyText: null,
                    BodyHtml: null,
                    IsInternal: true,
                    AuthorUserId: round.SentByUserId,
                    MetadataJson: JsonSerializer.Serialize(new
                    {
                        campaignId = round.CampaignId,
                        campaignName = round.CampaignName,
                        roundNo = round.RoundNo,
                        kind = round.Kind,
                        total = round.Total,
                        sent = round.Sent,
                        skipped = round.Skipped,
                        failed = round.Failed,
                        cancelled = round.Cancelled,
                    })), ct);
                if (evt is not null) await _ticketList.NotifyUpdatedAsync(projectId, ct);
            }

            await _audit.LogAsync(new AuditEvent(
                EventType: "campaign.round_completed",
                Actor: round.SentByUserId?.ToString() ?? "system",
                ActorRole: "system",
                Target: round.CampaignId.ToString(),
                Payload: new { round.RoundNo, round.Kind, round.Total, round.Sent, round.Skipped, round.Failed, round.Cancelled }), ct);
            await _notifier.CampaignChangedAsync(round.CampaignId, ct);
        }
    }
}
