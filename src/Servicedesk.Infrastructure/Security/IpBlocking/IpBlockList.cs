using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;

namespace Servicedesk.Infrastructure.Security.IpBlocking;

/// In-memory view of the IP rules, consulted on every request by the block
/// middleware — so lookups are lock-free reads of an immutable snapshot. The
/// DB stays the source of truth: the worker reloads it every minute (which
/// also picks up console break-glass edits) and admin actions apply their
/// change here immediately so a block takes effect on the next request.
public interface IIpBlockList
{
    bool IsBlocked(string ip, DateTime nowUtc);
    bool IsWhitelisted(string ip);
    /// Loopback, unspecified and the trusted reverse-proxy network(s) can
    /// never be blocked: blocking nginx's own address would lock out everyone.
    bool IsProtected(IPAddress address);
    bool IsProtected(string ip);

    void Replace(IEnumerable<IpRule> activeRules);
    void ApplyRule(string ip, string kind, DateTime? expiresUtc);
    void Remove(string ip);

    void RecordBlockedHit(string ip, DateTime nowUtc);
    IReadOnlyDictionary<string, (long Count, DateTime LastUtc)> DrainBlockedHits();

    int OpenProposalCount { get; }
    void SetOpenProposalCount(int count);
    (int Blocked, int Whitelisted) RuleCounts { get; }
}

public sealed class IpBlockList : IIpBlockList
{
    private sealed record Snapshot(
        FrozenDictionary<string, DateTime?> Blocks,
        FrozenSet<string> Whitelist);

    private static readonly Snapshot Empty = new(
        FrozenDictionary<string, DateTime?>.Empty, FrozenSet<string>.Empty);

    private readonly object _writeLock = new();
    private readonly IReadOnlyList<IPNetwork> _protectedNetworks;
    private volatile Snapshot _snapshot = Empty;
    private ConcurrentDictionary<string, HitCounter> _hits = new();
    private int _openProposals;

    private sealed class HitCounter
    {
        public long Count;
        public long LastTicks;
    }

    public IpBlockList(IEnumerable<IPNetwork>? protectedNetworks = null)
    {
        _protectedNetworks = (protectedNetworks ?? Array.Empty<IPNetwork>()).ToList();
    }

    public bool IsBlocked(string ip, DateTime nowUtc)
    {
        if (!_snapshot.Blocks.TryGetValue(ip, out var expires)) return false;
        return expires is null || expires.Value > nowUtc;
    }

    public bool IsWhitelisted(string ip) => _snapshot.Whitelist.Contains(ip);

    public bool IsProtected(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return true;
        foreach (var net in _protectedNetworks)
        {
            if (net.Contains(address)) return true;
        }
        return false;
    }

    public bool IsProtected(string ip) =>
        !IPAddress.TryParse(ip, out var address) || IsProtected(address);

    public void Replace(IEnumerable<IpRule> activeRules)
    {
        var blocks = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var whitelist = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in activeRules)
        {
            if (r.Kind == IpRuleKind.Whitelist) whitelist.Add(r.Ip);
            else if (r.Kind == IpRuleKind.Block && !IsProtected(r.Ip)) blocks[r.Ip] = r.ExpiresUtc;
        }
        lock (_writeLock)
        {
            _snapshot = new Snapshot(blocks.ToFrozenDictionary(StringComparer.Ordinal), whitelist.ToFrozenSet(StringComparer.Ordinal));
        }
    }

    public void ApplyRule(string ip, string kind, DateTime? expiresUtc)
    {
        lock (_writeLock)
        {
            var blocks = new Dictionary<string, DateTime?>(_snapshot.Blocks, StringComparer.Ordinal);
            var whitelist = new HashSet<string>(_snapshot.Whitelist, StringComparer.Ordinal);
            blocks.Remove(ip);
            whitelist.Remove(ip);
            if (kind == IpRuleKind.Whitelist) whitelist.Add(ip);
            else if (kind == IpRuleKind.Block && !IsProtected(ip)) blocks[ip] = expiresUtc;
            _snapshot = new Snapshot(blocks.ToFrozenDictionary(StringComparer.Ordinal), whitelist.ToFrozenSet(StringComparer.Ordinal));
        }
    }

    public void Remove(string ip) => ApplyRule(ip, kind: string.Empty, expiresUtc: null);

    public void RecordBlockedHit(string ip, DateTime nowUtc)
    {
        // Blocked requests are never audited one by one (a scanner would
        // flood the log); the counter is flushed to the rule row in batches.
        var c = _hits.GetOrAdd(ip, _ => new HitCounter());
        Interlocked.Increment(ref c.Count);
        Interlocked.Exchange(ref c.LastTicks, nowUtc.Ticks);
    }

    public IReadOnlyDictionary<string, (long Count, DateTime LastUtc)> DrainBlockedHits()
    {
        var drained = Interlocked.Exchange(ref _hits, new ConcurrentDictionary<string, HitCounter>());
        return drained.ToDictionary(
            kv => kv.Key,
            kv => (Interlocked.Read(ref kv.Value.Count), new DateTime(Interlocked.Read(ref kv.Value.LastTicks), DateTimeKind.Utc)),
            StringComparer.Ordinal);
    }

    public int OpenProposalCount => Volatile.Read(ref _openProposals);

    public void SetOpenProposalCount(int count) => Volatile.Write(ref _openProposals, Math.Max(0, count));

    public (int Blocked, int Whitelisted) RuleCounts
    {
        get
        {
            var s = _snapshot;
            return (s.Blocks.Count, s.Whitelist.Count);
        }
    }
}
