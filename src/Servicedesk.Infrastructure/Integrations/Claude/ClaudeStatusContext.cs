using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// One timeline row for the "Status update by AI" context.
internal sealed class StatusEventRow
{
    public string EventType { get; set; } = string.Empty;
    public string? BodyText { get; set; }
    public bool IsInternal { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid? AuthorUserId { get; set; }
    public Guid? AuthorContactId { get; set; }
    public string? UserName { get; set; }
    public string? ContactName { get; set; }
}

/// Pure formatting helpers for the status-update context: one line per
/// timeline event (with time and author), field changes rendered from their
/// metadata, and a budget fit that keeps the newest events.
public static class ClaudeStatusContext
{
    /// Metadata key + value stamped on the internal note the status update
    /// posts, so the next status update can leave earlier briefs out.
    public const string AiGeneratedKey = "aiGenerated";
    public const string AiGeneratedStatus = "status";

    /// Per-event body cap, same as the proposal/summary context.
    private const int MaxEventChars = 2000;

    /// Event types rendered as a one-line change from their metadata (they
    /// carry no body text). Every other event needs body text to be included.
    internal static readonly string[] ChangeEventTypes =
    {
        "StatusChange", "AssignmentChange", "PriorityChange", "QueueChange",
        "PendingTillChange", "TicketFlagChange",
    };

    /// "[time] who — what: text". Null when the event has nothing to say (a
    /// change event whose metadata is unreadable).
    internal static string? FormatEvent(StatusEventRow e, TimeZoneInfo tz)
    {
        var who = e.AuthorContactId.HasValue ? "Customer " + (e.ContactName ?? string.Empty)
            : e.AuthorUserId.HasValue ? "Agent " + (e.UserName ?? string.Empty)
            : "System";
        var prefix = "[" + FormatLocal(e.CreatedUtc, tz) + "] " + who.TrimEnd() + " — ";

        if (Array.IndexOf(ChangeEventTypes, e.EventType) >= 0)
        {
            var change = DescribeChange(e.EventType, e.MetadataJson, tz);
            return change is null ? null : prefix + change;
        }

        if (string.IsNullOrWhiteSpace(e.BodyText)) return null;
        var kind = e.IsInternal ? e.EventType + " (internal)" : e.EventType;
        return prefix + kind + ": " + Clamp(e.BodyText.Trim(), MaxEventChars);
    }

    /// Renders a field-change event's metadata as a short sentence.
    internal static string? DescribeChange(string eventType, string? metadataJson, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string Name(string name, string fallback) =>
                Str(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : fallback;
            string When(string name) =>
                Str(name) is { } raw
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
                    ? FormatLocal(utc, tz)
                    : "none";
            bool? Bool(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean()
                    : null;

            return eventType switch
            {
                "StatusChange" => $"Status changed: {Name("fromName", "?")} → {Name("toName", "?")}",
                "AssignmentChange" => $"Assignment changed: {Name("fromName", "nobody")} → {Name("toName", "nobody")}",
                "PriorityChange" => $"Priority changed: {Name("fromName", "?")} → {Name("toName", "?")}",
                "QueueChange" => $"Queue changed: {Name("fromName", "?")} → {Name("toName", "?")}",
                "PendingTillChange" => $"Pending till changed: {When("from")} → {When("to")}",
                "TicketFlagChange" => Str("flag") is { } flag && Bool("to") is { } on
                    ? $"{FlagLabel(flag)} flag {(on ? "set" : "cleared")}"
                    : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FlagLabel(string flag) => flag switch
    {
        "callback" => "Call-back",
        "research" => "Research",
        "descriptionInternal" => "Description internal",
        _ => flag,
    };

    /// Fits the timeline into <paramref name="budget"/> characters. When it
    /// all fits every line is kept. Otherwise the first line (how the ticket
    /// started) and as many of the newest lines as fit are kept, with a marker
    /// for the gap: for a status update the recent history matters most.
    internal static string FitTimeline(IReadOnlyList<string> lines, int budget)
    {
        if (lines.Count == 0 || budget <= 0) return string.Empty;
        var total = lines.Sum(l => l.Length + 1);
        if (total <= budget) return string.Join('\n', lines) + "\n";

        const int markerReserve = 64;
        var first = lines[0];
        // Keep the opening event only when it leaves most of the room for
        // the recent history.
        var keepFirst = first.Length + 1 + markerReserve <= budget / 2;
        var remaining = budget - markerReserve - (keepFirst ? first.Length + 1 : 0);
        var lowest = keepFirst ? 1 : 0;

        var tail = new List<string>();
        for (var i = lines.Count - 1; i >= lowest; i--)
        {
            var cost = lines[i].Length + 1;
            if (cost > remaining) break;
            tail.Add(lines[i]);
            remaining -= cost;
        }
        tail.Reverse();

        var omitted = lines.Count - lowest - tail.Count;
        var sb = new StringBuilder();
        if (keepFirst) sb.Append(first).Append('\n');
        if (omitted > 0) sb.Append("…[").Append(omitted).Append(" earlier events left out]…\n");
        foreach (var line in tail) sb.Append(line).Append('\n');
        return sb.ToString();
    }

    internal static string FormatLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Clamp(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…[truncated]";
}
