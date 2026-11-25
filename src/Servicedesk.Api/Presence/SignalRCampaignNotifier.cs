using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Campaigns;

namespace Servicedesk.Api.Presence;

/// v0.1.67 — SignalR-backed <see cref="ICampaignNotifier"/>: a
/// <c>CampaignChanged(campaignId)</c> ping to the <c>campaigns</c> group that
/// <see cref="UserNotificationHub"/> joins Campaigns flag holders to on
/// connect. Only the id travels; the page refetches through the gated API.
public sealed class SignalRCampaignNotifier : ICampaignNotifier
{
    public const string Group = "campaigns";

    private readonly IHubContext<UserNotificationHub> _hub;

    public SignalRCampaignNotifier(IHubContext<UserNotificationHub> hub) => _hub = hub;

    public Task CampaignChangedAsync(Guid campaignId, CancellationToken ct)
        => _hub.Clients.Group(Group).SendAsync("CampaignChanged", campaignId.ToString(), ct);
}
