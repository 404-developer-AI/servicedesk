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
            (@Utc, @Actor, @ActorRole, @EventType, @Target, @ClientIp, @UserAgent, @Payload::jsonb, @PrevHash, @EntryHash)
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

        var utc = DateTimeOffset.UtcNow;
        var payloadJson = evt.Payload is null
            ? "{}"
            : JsonSerializer.Serialize(evt.Payload);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(@key)",
                new { key = AuditLockKey },
                transaction,
                cancellationToken: cancellationToken));

        var prevHash = await connection.QueryFirstOrDefaultAsync<byte[]?>(
            new CommandDefinition(
                SelectLastHashSql,
                transaction: transaction,
                cancellationToken: cancellationToken))
            ?? GenesisHash;

        var entryHash = ComputeHash(
            key,
            prevHash,
            utc,
            evt.Actor,
            evt.ActorRole,
            evt.EventType,
            evt.Target,
            evt.ClientIp,
            evt.UserAgent,
            payloadJson);

        await connection.ExecuteAsync(
            new CommandDefinition(
                InsertSql,
                new
                {
                    Utc = utc,
                    Actor = evt.Actor ?? "",
                    ActorRole = evt.ActorRole ?? "",
                    evt.EventType,
                    evt.Target,
                    evt.ClientIp,
                    evt.UserAgent,
                    Payload = payloadJson,
                    PrevHash = prevHash,
                    EntryHash = entryHash,
                },
                transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
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
