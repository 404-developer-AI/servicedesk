using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Builds the metadata JSON written into <c>ticket_events.metadata</c> by
/// trigger-fired mutations. The agent UI's manual change-events use the same
/// <c>{ from, to, fromName, toName }</c> shape (see TicketRepository.UpdateFieldsAsync);
/// the trigger path adds <c>triggered_by</c> so the timeline can render a
/// "by trigger {id}" badge instead of an agent avatar.
internal static class TriggerEventMetadata
{
    public static string FieldChange(
        Guid? fromId,
        Guid? toId,
        string? fromName,
        string? toName,
        Guid triggerId,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["from"] = fromId,
            ["to"] = toId,
            ["fromName"] = fromName,
            ["toName"] = toName,
            ["triggered_by"] = triggerId,
        };
        if (extra is not null)
        {
            foreach (var kv in extra) payload[kv.Key] = kv.Value;
        }
        return JsonSerializer.Serialize(payload);
    }

    public static string SystemNote(Guid triggerId, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["triggered_by"] = triggerId,
        };
        if (extra is not null)
        {
            foreach (var kv in extra) payload[kv.Key] = kv.Value;
        }
        return JsonSerializer.Serialize(payload);
    }
}
