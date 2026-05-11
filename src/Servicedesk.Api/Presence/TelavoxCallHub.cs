using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Servicedesk.Api.Presence;

/// Per-user SignalR hub for the Telavox call-popup channel (v0.0.34
/// commit C). On connect the hub reads the user-id from the caller's
/// session claims and auto-joins the <c>telavox-user:{id}</c> group so
/// <see cref="Infrastructure.Realtime.ITelavoxCallNotifier"/> can push
/// <c>TelavoxCall</c> events to exactly that agent.
///
/// <para>
/// Pure server-push surface — no client-invoked methods. Authorize policy
/// is <c>RequireAgent</c> so a future customer-portal user can never
/// silently subscribe to an agent's call-stream by guessing the hub URL.
/// </para>
[Authorize(Policy = "RequireAgent")]
public sealed class TelavoxCallHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"telavox-user:{userId}");
        }
        await base.OnConnectedAsync();
    }
}
