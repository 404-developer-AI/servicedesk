namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — whether one ticket may receive a campaign mail. Pure; the
/// same rules run when a round is queued and again right before each send
/// (the ticket can change in between). Mirrors the single-ticket mail rules:
/// project tickets never mail a customer (<see cref="Tickets.ProjectMailGuard"/>,
/// passed in as <paramref name="projectMailBlocked"/>), no mail on a closed
/// or merged ticket, and there must be a requester address.
public static class CampaignEligibility
{
    public static string? Check(
        bool projectMailBlocked, bool isMerged, string stateCategory, string? requesterEmail,
        IReadOnlyCollection<string>? missingTokens = null)
    {
        if (projectMailBlocked) return CampaignSkipReasons.ProjectTicket;
        if (isMerged) return CampaignSkipReasons.Merged;
        if (string.Equals(stateCategory, "Closed", StringComparison.Ordinal)) return CampaignSkipReasons.Closed;
        if (string.IsNullOrWhiteSpace(requesterEmail) || !requesterEmail.Contains('@'))
            return CampaignSkipReasons.NoRequesterEmail;
        if (missingTokens is { Count: > 0 }) return CampaignSkipReasons.MissingTokens;
        return null;
    }
}
