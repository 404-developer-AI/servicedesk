using Microsoft.AspNetCore.SignalR;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Api.Presence;

/// SignalR-backed <see cref="IBackofficeFormNotifier"/>: a payload-less
/// <c>BackofficeFormsChanged</c> to the <c>backoffice-forms</c> group that
/// <see cref="UserNotificationHub"/> joins flag holders to on connect.
public sealed class SignalRBackofficeFormNotifier : IBackofficeFormNotifier
{
    public const string Group = "backoffice-forms";

    private readonly IHubContext<UserNotificationHub> _hub;

    public SignalRBackofficeFormNotifier(IHubContext<UserNotificationHub> hub) => _hub = hub;

    public Task BackofficeFormsChangedAsync(CancellationToken ct)
        => _hub.Clients.Group(Group).SendAsync("BackofficeFormsChanged", ct);
}
