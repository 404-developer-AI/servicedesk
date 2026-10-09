using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

namespace Servicedesk.Infrastructure.Security.IpBlocking;

/// Live configuration of the detector, refreshed from settings by the
/// worker (settings reads are cached anyway; this just keeps the per-request
/// path free of async calls).
public sealed record IpThreatConfig(
    bool Enabled,
    TimeSpan Window,
    int ScannerThreshold,
    int RateLimitThreshold,
    int CsrfThreshold,
    int FailedLoginThreshold,
    TimeSpan AutoBlockDuration,
    ScannerPathMatcher? ScannerPathPattern)
{
    public static readonly IpThreatConfig Disabled = new(
        false, TimeSpan.FromMinutes(10), 2, 100, 10, 10, TimeSpan.FromHours(24), null);

    public int ThresholdFor(IpSignalKind kind) => kind switch
    {
        IpSignalKind.ScannerPath => ScannerThreshold,
        IpSignalKind.RateLimited => RateLimitThreshold,
        IpSignalKind.CsrfRejected => CsrfThreshold,
        IpSignalKind.FailedLogin => FailedLoginThreshold,
        _ => int.MaxValue,
    };

    /// Parses the comma-separated glob list ("/.env*, *.php, …") into a
    /// case-insensitive, anchored matcher. `*` matches any characters; every
    /// other character is literal. Empty input → null (no scanner detection).
    public static ScannerPathMatcher? CompileScannerPatterns(string? patterns) =>
        ScannerPathMatcher.Parse(patterns);
}

/// v0.1.33 — glob matching without a regex. The list used to be one
/// Compiled regex with a 50 ms match timeout; the timeout counts wall-clock
/// time, so a JIT or GC pause during a match made IsScannerPath silently
/// answer "no" (a scanner probe went unnoticed; the tests failed at random
/// under load). A NonBacktracking regex instead exceeded .NET's automaton
/// size limit on the default list. Globs with only `*` need neither: split
/// on `*`, the first piece must be a prefix, the last a suffix, the middle
/// pieces are found left to right — greedy leftmost matching is exact for
/// `*`-only patterns, linear in the path length, with no timeout and no size
/// limit.
public sealed class ScannerPathMatcher
{
    private readonly string[][] _patterns;

    private ScannerPathMatcher(string[][] patterns) => _patterns = patterns;

    public int Count => _patterns.Length;

    public static ScannerPathMatcher? Parse(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return null;
        var parsed = patterns
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0 && p.Trim('*').Length > 0) // "*" alone would match everything
            .Select(p => p.Split('*'))
            .ToArray();
        return parsed.Length == 0 ? null : new ScannerPathMatcher(parsed);
    }

    public bool IsMatch(string path)
    {
        foreach (var pieces in _patterns)
        {
            if (Matches(path, pieces)) return true;
        }
        return false;
    }

    // pieces = pattern.Split('*'): no star → one piece (exact match).
    private static bool Matches(string path, string[] pieces)
    {
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        if (pieces.Length == 1) return string.Equals(path, pieces[0], cmp);

        var first = pieces[0];
        var last = pieces[^1];
        if (path.Length < first.Length + last.Length) return false;
        if (!path.StartsWith(first, cmp) || !path.EndsWith(last, cmp)) return false;

        var pos = first.Length;
        var end = path.Length - last.Length;
        for (var i = 1; i < pieces.Length - 1; i++)
        {
            var piece = pieces[i];
            if (piece.Length == 0) continue;
            var at = path.IndexOf(piece, pos, end - pos, cmp);
            if (at < 0) return false;
            pos = at + piece.Length;
        }
        return true;
    }
}

public interface IIpThreatDetector
{
    IpThreatConfig Config { get; }
    void UpdateConfig(IpThreatConfig config);

    bool IsScannerPath(string? path);
    /// Records one signal. Cheap and synchronous — safe on the request path.
    void Record(IPAddress address, IpSignalKind kind, string? path, DateTime nowUtc);
    void Record(string? ip, IpSignalKind kind, string? path, DateTime nowUtc);

    /// Observations whose threshold was crossed, for the worker.
    ChannelReader<IpThreatObservation> Tripped { get; }

    /// Drops idle per-IP state; returns the number of IPs still tracked.
    int Sweep(DateTime nowUtc);
}

public sealed class IpThreatDetector : IIpThreatDetector
{
    /// Hard ceiling on tracked IPs so a distributed scan can't grow memory
    /// without bound. Beyond it new IPs are ignored until the next sweep —
    /// the existing auth/rate-limit defences still apply to them.
    internal const int MaxTrackedIps = 20_000;
    private const int MaxSamplePaths = 10;
    private const int MaxPathLength = 200;

    private readonly IIpBlockList _blockList;
    private readonly ConcurrentDictionary<string, IpState> _states = new(StringComparer.Ordinal);
    private readonly Channel<IpThreatObservation> _tripped = Channel.CreateBounded<IpThreatObservation>(
        new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private volatile IpThreatConfig _config = IpThreatConfig.Disabled;

    public IpThreatDetector(IIpBlockList blockList)
    {
        _blockList = blockList;
    }

    public IpThreatConfig Config => _config;

    public void UpdateConfig(IpThreatConfig config) => _config = config;

    public ChannelReader<IpThreatObservation> Tripped => _tripped.Reader;

    public bool IsScannerPath(string? path)
    {
        var matcher = _config.ScannerPathPattern;
        if (matcher is null || string.IsNullOrEmpty(path)) return false;
        return matcher.IsMatch(path);
    }

    public void Record(string? ip, IpSignalKind kind, string? path, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address)) return;
        Record(address, kind, path, nowUtc);
    }

    public void Record(IPAddress address, IpSignalKind kind, string? path, DateTime nowUtc)
    {
        var config = _config;
        if (!config.Enabled) return;
        if (_blockList.IsProtected(address)) return;

        var ip = IpAddressText.Normalize(address);
        if (_blockList.IsWhitelisted(ip)) return;

        if (!_states.TryGetValue(ip, out var state))
        {
            if (_states.Count >= MaxTrackedIps) return;
            state = _states.GetOrAdd(ip, _ => new IpState(nowUtc));
        }

        IpThreatObservation? observation = null;
        lock (state)
        {
            state.Add(kind, path, nowUtc, config.Window);
            var count = state.Count(kind);
            // Report each kind once per window episode; a fresh episode (the
            // count fell back under the threshold) may report again, which
            // simply refreshes the open proposal.
            if (count >= config.ThresholdFor(kind) && state.Reported.Add(kind))
            {
                observation = state.ToObservation(ip, kind);
            }
        }

        if (observation is not null) _tripped.Writer.TryWrite(observation);
    }

    public int Sweep(DateTime nowUtc)
    {
        var window = _config.Window;
        foreach (var (ip, state) in _states)
        {
            bool idle;
            lock (state)
            {
                state.Trim(nowUtc, window);
                idle = state.IsEmpty;
            }
            if (idle) _states.TryRemove(ip, out _);
        }
        return _states.Count;
    }

    private sealed class IpState
    {
        private readonly Dictionary<IpSignalKind, Queue<DateTime>> _events = new();
        private readonly Queue<string> _paths = new();
        public readonly HashSet<IpSignalKind> Reported = new();
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;

        public IpState(DateTime nowUtc)
        {
            FirstSeenUtc = nowUtc;
            LastSeenUtc = nowUtc;
        }

        public bool IsEmpty => _events.Values.All(q => q.Count == 0);

        public void Add(IpSignalKind kind, string? path, DateTime nowUtc, TimeSpan window)
        {
            Trim(nowUtc, window);
            if (!_events.TryGetValue(kind, out var q)) _events[kind] = q = new Queue<DateTime>();
            q.Enqueue(nowUtc);
            LastSeenUtc = nowUtc;
            if (!string.IsNullOrEmpty(path))
            {
                var p = path.Length > MaxPathLength ? path[..MaxPathLength] : path;
                if (!_paths.Contains(p))
                {
                    _paths.Enqueue(p);
                    while (_paths.Count > MaxSamplePaths) _paths.Dequeue();
                }
            }
        }

        public int Count(IpSignalKind kind) => _events.TryGetValue(kind, out var q) ? q.Count : 0;

        public void Trim(DateTime nowUtc, TimeSpan window)
        {
            var cutoff = nowUtc - window;
            foreach (var (kind, q) in _events)
            {
                while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
                if (q.Count == 0) Reported.Remove(kind);
            }
            if (IsEmpty)
            {
                _paths.Clear();
                FirstSeenUtc = nowUtc;
            }
        }

        public IpThreatObservation ToObservation(string ip, IpSignalKind trippedKind)
        {
            var counts = _events.Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            return new IpThreatObservation(ip, counts, _paths.ToList(), new[] { trippedKind },
                FirstSeenUtc, LastSeenUtc);
        }
    }
}
