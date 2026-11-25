namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — payload-less "campaign changed" ping for open Campaigns pages.
/// Carries only the campaign id; the page refetches through the
/// flag-gated, queue-scoped API, so a late flag revoke leaks nothing.
public interface ICampaignNotifier
{
    Task CampaignChangedAsync(Guid campaignId, CancellationToken ct);
}

public sealed class NullCampaignNotifier : ICampaignNotifier
{
    public Task CampaignChangedAsync(Guid campaignId, CancellationToken ct) => Task.CompletedTask;
}

/// Wakes <see cref="CampaignSendWorker"/> right after a round is queued.
public sealed class CampaignSendSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        if (_signal.CurrentCount != 0) return;
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
