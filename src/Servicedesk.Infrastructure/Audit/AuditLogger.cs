using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Secrets;

namespace Servicedesk.Infrastructure.Audit;

/// <summary>
/// Append-only audit writer. Each row's <c>entry_hash</c> is
/// <c>HMAC-SHA256(key, prev_hash || canonical_fields)</c>, forming a tamper-evident
/// chain: changing any earlier row invalidates every subsequent hash.
/// </summary>
/// <remarks>
/// Writes happen inside a transaction under an advisory lock
/// (<c>pg_advisory_xact_lock(AuditLockKey)</c>) so concurrent writers chain
/// correctly without a race between "read last hash" and "insert".
/// Dapper is used instead of EF Core to avoid change-tracking overhead on a
/// hot-path write that never reads the row back.
/// </remarks>
public sealed class AuditLogger : IAuditLogger
{
    private const long AuditLockKey = 0x5EC_A0D17_1065_E11L;

    private const string InsertSql = """
        INSERT INTO audit_log
            (utc, actor, actor_role, event_type, target, client_ip, user_agent, payload, prev_hash, entry_hash)
        VALUES
            ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10)
        """;

    private const string SelectLastHashSql = """
        SELECT entry_hash FROM audit_log ORDER BY id DESC LIMIT 1
        """;

    private static readonly byte[] GenesisHash = new byte[32];

    private readonly NpgsqlDataSource _dataSource;
    private readonly ISecretProvider _secrets;

    public AuditLogger(NpgsqlDataSource dataSource, ISecretProvider secrets)
    {
        _dataSource = dataSource;
        _secrets = secrets;
    }

    public async Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.EventType))
        {
            throw new ArgumentException("EventType is required.", nameof(evt));
        }

        var keyBase64 = _secrets.GetRequired("Audit:HashKey");
        var key = DecodeKey(keyBase64);

        var pending = new PendingEntry(
            evt,
            DateTimeOffset.UtcNow,
            evt.Payload is null ? "{}" : JsonSerializer.Serialize(evt.Payload),
            key);
        _queue.Enqueue(pending);

        // v0.1.25 — group commit. The chain needs one writer at a time (the
        // advisory lock), so concurrent callers used to queue on the lock one
        // transaction + fsync each: opening a ticket with 20 inline images
        // meant 20 serialised audit transactions, ~51 ms average lock wait
        // in the Performance monitor. Now whoever gets the gate writes every
        // entry queued so far in ONE transaction (same lock, same chaining
        // order, same rows); the others find their entry already committed.
        // Every caller still returns only after its own row is durable, so
        // "audited before the response" is unchanged.
        await _flushGate.WaitAsync(CancellationToken.None);
        try
        {
            if (!pending.Done.Task.IsCompleted)
                await FlushQueuedAsync();
        }
        finally
        {
            _flushGate.Release();
        }
        await pending.Done.Task;
    }

    private const int MaxEntriesPerTransaction = 200;

    private readonly ConcurrentQueue<PendingEntry> _queue = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private sealed record PendingEntry(AuditEvent Evt, DateTimeOffset Utc, string PayloadJson, byte[] Key)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// Drains the queue in transactions of up to <see cref="MaxEntriesPerTransaction"/>.
    /// Runs under <see cref="_flushGate"/>, never with a caller's
    /// cancellation token: one request aborting must not fail the audit rows
    /// of the other requests in the same batch.
    private async Task FlushQueuedAsync()
    {
        while (!_queue.IsEmpty)
        {
            var batch = new List<PendingEntry>();
            while (batch.Count < MaxEntriesPerTransaction && _queue.TryDequeue(out var e)) batch.Add(e);
            if (batch.Count == 0) return;
            try
            {
                await WriteBatchAsync(batch);
                foreach (var e in batch) e.Done.TrySetResult();
            }
            catch (Exception ex)
            {
                foreach (var e in batch) e.Done.TrySetException(ex);
            }
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<PendingEntry> entries)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(@key)",
                new { key = AuditLockKey },
                transaction));

        var prevHash = await connection.QueryFirstOrDefaultAsync<byte[]?>(
            new CommandDefinition(
                SelectLastHashSql,
                transaction: transaction))
            ?? GenesisHash;

        // Chain in queue order: each entry's prev_hash is the previous
        // entry's entry_hash, exactly as consecutive single writes would.
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var p in entries)
        {
            var evt = p.Evt;
            var entryHash = ComputeHash(
                p.Key,
                prevHash,
                p.Utc,
                evt.Actor,
                evt.ActorRole,
                evt.EventType,
                evt.Target,
                evt.ClientIp,
                evt.UserAgent,
                p.PayloadJson);

            batch.BatchCommands.Add(new NpgsqlBatchCommand(InsertSql)
            {
                Parameters =
                {
                    new() { Value = p.Utc },
                    new() { Value = evt.Actor ?? "" },
                    new() { Value = evt.ActorRole ?? "" },
                    new() { Value = evt.EventType },
                    new() { Value = (object?)evt.Target ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                    new() { Value = (object?)evt.ClientIp ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                    new() { Value = (object?)evt.UserAgent ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                    new() { Value = p.PayloadJson },
                    new() { Value = prevHash },
                    new() { Value = entryHash },
                },
            });
            prevHash = entryHash;
        }
        await batch.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    internal static byte[] ComputeHash(
        byte[] key,
        byte[] prevHash,
        DateTimeOffset utc,
        string actor,
        string actorRole,
        string eventType,
        string? target,
        string? clientIp,
        string? userAgent,
        string payloadJson)
    {
        // Canonical form: length-prefixed fields so "abc"|"def" can never collide
        // with "ab"|"cdef". Each field is written as <int32 length><bytes>.
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(prevHash.Length);
            writer.Write(prevHash);
            WriteField(writer, utc.UtcDateTime.ToString("O"));
            WriteField(writer, actor);
            WriteField(writer, actorRole);
            WriteField(writer, eventType);
            WriteField(writer, target ?? "");
            WriteField(writer, clientIp ?? "");
            WriteField(writer, userAgent ?? "");
            WriteField(writer, payloadJson);
        }

        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(ms.ToArray());
    }

    private static void WriteField(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] DecodeKey(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            // Treat non-base64 keys as raw UTF-8 bytes — keeps dev setup forgiving
            // without sacrificing production strength (ops uses `openssl rand -base64 32`).
            return Encoding.UTF8.GetBytes(base64);
        }
    }
}
