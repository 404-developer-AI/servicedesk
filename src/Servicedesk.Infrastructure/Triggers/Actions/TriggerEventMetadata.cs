using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Builds the metadata JSON for trigger-written notes and system notes:
/// <c>triggered_by</c> so the timeline renders a "by trigger" badge instead
/// of an agent avatar, plus <c>on_behalf_of</c> (v0.1.32, see
/// <see cref="TriggerOrigin"/>). Field-change events get the same two keys
/// from <see cref="Persistence.Tickets.TicketFieldWriter"/>.
internal static class TriggerEventMetadata
{
    public static string SystemNote(Guid triggerId, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["triggered_by"] = triggerId,
        };
        // v0.1.32 — the agent whose action caused this pass (request path only).
        if (TriggerOrigin.OnBehalfOfUserId is { } agent) payload["on_behalf_of"] = agent;
        if (extra is not null)
        {
            foreach (var kv in extra) payload[kv.Key] = kv.Value;
        }
        return JsonSerializer.Serialize(payload);
    }
}
