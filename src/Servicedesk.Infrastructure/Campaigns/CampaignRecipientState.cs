namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — the dashboard state of one campaign recipient, derived at read
/// time (nothing is written per ticket change). Order matters:
///  1. never queued                           → NotSent
///  2. mailed once and the customer answered  → ReplyReceived (a mail or
///     portal message after the first successful send, auto-replies ignored)
///  3. latest outbox row still queued/sending → Queued
///  4. latest outbox row failed / skipped     → Failed / Skipped
///  5. mailed, no answer and the round's "pending till" has passed (the
///     reminder trigger reopened it, or it is due) → TimerExpired
///  6. otherwise                              → Waiting
public static class CampaignRecipientState
{
    public static string Derive(CampaignRecipientRow r, DateTime nowUtc)
    {
        if (r.MessageState is null) return CampaignRecipientStates.NotSent;

        if (r.FirstSentUtc is not null && r.ReplyUtc is not null)
            return CampaignRecipientStates.ReplyReceived;

        switch (r.MessageState)
        {
            case CampaignMessageStates.Queued:
            case CampaignMessageStates.Sending:
                return CampaignRecipientStates.Queued;
            case CampaignMessageStates.Failed:
                return CampaignRecipientStates.Failed;
            case CampaignMessageStates.Skipped:
            case CampaignMessageStates.Cancelled:
                return r.FirstSentUtc is null ? CampaignRecipientStates.Skipped : SentState(r, nowUtc);
        }

        return r.FirstSentUtc is null ? CampaignRecipientStates.NotSent : SentState(r, nowUtc);
    }

    private static string SentState(CampaignRecipientRow r, DateTime nowUtc)
    {
        var expired = r.RoundPendingTillUtc is { } till && till <= nowUtc;
        return expired ? CampaignRecipientStates.TimerExpired : CampaignRecipientStates.Waiting;
    }
}
