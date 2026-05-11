using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed implementation of <see cref="ITelavoxCallNotifier"/>.
/// Pushes <c>TelavoxCall</c> to the per-user group maintained by
/// <see cref="TelavoxCallHub"/>. The method-name is intentionally generic
/// so the SPA can dispatch on the payload's <c>State</c> field rather than
/// listening for one method per trigger-mode.
public sealed class SignalRTelavoxCallNotifier : ITelavoxCallNotifier
{
    private readonly IHubContext<TelavoxCallHub> _hub;

    public SignalRTelavoxCallNotifier(IHubContext<TelavoxCallHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyCallAsync(Guid userId, TelavoxCallEventPayload payload, CancellationToken ct)
        => _hub.Clients.Group($"telavox-user:{userId}")
            .SendAsync("TelavoxCall", payload, ct);
}
