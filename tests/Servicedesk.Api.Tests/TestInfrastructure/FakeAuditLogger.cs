using System.Collections.Concurrent;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Tests.TestInfrastructure;

public sealed class FakeAuditLogger : IAuditLogger
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public IReadOnlyCollection<AuditEvent> Events => _events.ToArray();

    public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(evt);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
    }
}

public sealed class FakeAuditQuery : IAuditQuery
{
    private readonly List<AuditLogEntry> _entries = new();
    private long _nextId = 1;

    public void Add(AuditLogEntry entry) => _entries.Add(entry);

    public void Seed(string eventType, string actor, string actorRole, string? target = null)
    {
        _entries.Add(new AuditLogEntry(
            Id: _nextId++,
            Utc: DateTime.UtcNow,
            Actor: actor,
            ActorRole: actorRole,
            EventType: eventType,
            Target: target,
            ClientIp: null,
            UserAgent: null,
            PayloadJson: "{}",
            PrevHash: new byte[32],
            EntryHash: new byte[32]));
    }

    public Task<AuditPage> ListAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        var items = _entries.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            items = items.Where(e => e.EventType == query.EventType);
        }
        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            items = items.Where(e => e.Actor == query.Actor);
        }
        var list = items.OrderByDescending(e => e.Id).Take(query.Limit).ToList();
        return Task.FromResult(new AuditPage(list, null));
    }

    public Task<AuditLogEntry?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

    public Task<AuditPage> ListForContactAsync(
        Guid contactId, long? cursorId, int limit, CancellationToken cancellationToken = default)
    {
        var contactText = contactId.ToString();
        var items = _entries.Where(e =>
            e.Target == contactText
            || (e.PayloadJson.Contains($"\"contactId\":\"{contactText}\"", StringComparison.OrdinalIgnoreCase)));
        if (cursorId is not null)
            items = items.Where(e => e.Id < cursorId.Value);
        var list = items.OrderByDescending(e => e.Id).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new AuditPage(list, null));
    }

    public Task<IReadOnlyDictionary<string, int>> CountByEventTypesAsync(
        IReadOnlyCollection<string> eventTypes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var types = new HashSet<string>(eventTypes, StringComparer.Ordinal);
        var dict = _entries
            .Where(e => types.Contains(e.EventType))
            .Where(e => e.Utc >= fromUtc.UtcDateTime && e.Utc < toUtc.UtcDateTime)
            .GroupBy(e => e.EventType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, int>>(dict);
    }
}
