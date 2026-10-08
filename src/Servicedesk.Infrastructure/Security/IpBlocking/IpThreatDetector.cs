using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
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
    Regex? ScannerPathPattern)
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

    /// Compiles the comma-separated glob list ("/.env*, *.php, …") into one
    /// case-insensitive, anchored regex. `*` matches any characters; every
    /// other character is literal. Invalid/empty input → null (no scanner
    /// detection rather than a crash).
    public static Regex? CompileScannerPatterns(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return null;
        var parts = patterns
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0 && p != "*")
            .Select(p => "^" + Regex.Escape(p).Replace("\\*", ".*") + "$")
            .ToList();
        if (parts.Count == 0) return null;
        return new Regex(string.Join("|", parts),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(50));
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
        var rx = _config.ScannerPathPattern;
        if (rx is null || string.IsNullOrEmpty(path)) return false;
        try
        {
            return rx.IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
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
