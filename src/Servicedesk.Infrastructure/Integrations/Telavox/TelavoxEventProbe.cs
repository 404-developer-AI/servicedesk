namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// One field Telavox sent (query string, form or JSON body), masked.
public sealed record TelavoxProbeField(string Source, string Name, string Value);

/// One call event as it arrived at the probe endpoint.
public sealed record TelavoxProbeEvent(
    DateTime ReceivedUtc,
    string Method,
    string? ContentType,
    IReadOnlyList<TelavoxProbeField> Fields,
    string? AgentEmail,
    bool? AgentMatchesUser);

/// v0.1.36 — Telavox call-event webhook PROBE: a log-only receiver that
/// keeps the last events in memory so an admin can see what Telavox really
/// sends (event names, direction, timestamps, a call id or not, whether the
/// agent e-mail matches a Servicedesk user) before the push-based popup is
/// built. Nothing is persisted and nothing acts on the events; a restart
/// empties it. Phone numbers are masked to their last three digits on the
/// way in, so customer numbers never sit in memory in full.
public sealed class TelavoxEventProbeBuffer
{
    public const int Capacity = 50;
    private const int MaxFields = 30;
    private const int MaxNameLength = 64;
    private const int MaxValueLength = 256;

    private readonly object _gate = new();
    private readonly LinkedList<TelavoxProbeEvent> _events = new();
    private long _received;

    public void Add(TelavoxProbeEvent evt)
    {
        lock (_gate)
        {
            _received++;
            _events.AddFirst(evt);
            while (_events.Count > Capacity) _events.RemoveLast();
        }
    }

    public (IReadOnlyList<TelavoxProbeEvent> Events, long Received) Snapshot()
    {
        lock (_gate) return (_events.ToList(), _received);
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
    }

    /// Bounds and masks the raw name/value pairs of one request.
    public static IReadOnlyList<TelavoxProbeField> Sanitize(IEnumerable<(string Source, string Name, string? Value)> raw) =>
        raw.Take(MaxFields)
           .Select(f => new TelavoxProbeField(
               f.Source,
               Truncate(f.Name, MaxNameLength),
               IsTimeField(f.Name)
                   ? Truncate(f.Value ?? string.Empty, MaxValueLength)
                   : MaskPhone(Truncate(f.Value ?? string.Empty, MaxValueLength))))
           .ToList();

    /// Timestamps are digit runs too, but the probe needs them in full to
    /// measure latency — a field named like a time is never masked.
    private static bool IsTimeField(string name) =>
        name.Equals("ts", StringComparison.OrdinalIgnoreCase)
        || name.Contains("time", StringComparison.OrdinalIgnoreCase)
        || name.Contains("stamp", StringComparison.OrdinalIgnoreCase);

    /// "+32470123456" → "+32•••••••456". Only a bare phone-like value
    /// (digits with + ( ) . - and spaces, at least six digits) is masked;
    /// event names, e-mail addresses and ISO timestamps pass through; a unix
    /// timestamp is only kept when its field is named like a time.
    public static string MaskPhone(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || !trimmed.All(c => char.IsDigit(c) || c is '+' or ' ' or '(' or ')' or '.' or '-'))
            return value;
        var digits = trimmed.Count(char.IsDigit);
        if (digits < 6) return value;
        var keepLeading = trimmed.StartsWith('+') ? 2 : 0; // the country code
        var chars = trimmed.ToCharArray();
        var seen = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsDigit(chars[i])) continue;
            seen++;
            if (seen > keepLeading && digits - seen >= 3) chars[i] = '•';
        }
        return new string(chars);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
