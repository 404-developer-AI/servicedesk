using System.Text;
using Servicedesk.Infrastructure.Audit;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Unit tests for the HMAC hash-chain helper. DB-level integration tests of
/// <see cref="AuditLogger"/> (concurrent writes under advisory lock, Dapper
/// insert path) land in v0.0.5 alongside the Testcontainers test harness.
public sealed class AuditLoggerHashChainTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-key-32-bytes-long-123456789");
    private static readonly byte[] Genesis = new byte[32];
    private static readonly DateTimeOffset Utc = new(2026, 4, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameInputs_ProduceSameHash()
    {
        var a = AuditLogger.ComputeHash(Key, Genesis, Utc, "alice", "Admin", "login", null, "127.0.0.1", "ua", "{}");
        var b = AuditLogger.ComputeHash(Key, Genesis, Utc, "alice", "Admin", "login", null, "127.0.0.1", "ua", "{}");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ChangingAnyField_ChangesHash()
    {
        var baseline = AuditLogger.ComputeHash(Key, Genesis, Utc, "alice", "Admin", "login", null, "127.0.0.1", "ua", "{}");
        Assert.NotEqual(baseline, AuditLogger.ComputeHash(Key, Genesis, Utc, "bob",   "Admin", "login", null, "127.0.0.1", "ua", "{}"));
        Assert.NotEqual(baseline, AuditLogger.ComputeHash(Key, Genesis, Utc, "alice", "Agent", "login", null, "127.0.0.1", "ua", "{}"));
        Assert.NotEqual(baseline, AuditLogger.ComputeHash(Key, Genesis, Utc, "alice", "Admin", "logout", null, "127.0.0.1", "ua", "{}"));
        Assert.NotEqual(baseline, AuditLogger.ComputeHash(Key, Genesis, Utc, "alice", "Admin", "login", null, "127.0.0.1", "ua", "{\"x\":1}"));
    }

    [Fact]
    public void ChainingPrevHash_PropagatesForward()
    {
        var h1 = AuditLogger.ComputeHash(Key, Genesis, Utc, "a", "Admin", "e1", null, null, null, "{}");
        var h2 = AuditLogger.ComputeHash(Key, h1,      Utc, "a", "Admin", "e2", null, null, null, "{}");
        var h3 = AuditLogger.ComputeHash(Key, h2,      Utc, "a", "Admin", "e3", null, null, null, "{}");

        // Tamper with h1 by recomputing with different actor; h2 recomputed from
        // the tampered h1 must not match the original h2.
        var tamperedH1 = AuditLogger.ComputeHash(Key, Genesis, Utc, "X", "Admin", "e1", null, null, null, "{}");
        var tamperedH2 = AuditLogger.ComputeHash(Key, tamperedH1, Utc, "a", "Admin", "e2", null, null, null, "{}");
        Assert.NotEqual(h2, tamperedH2);

        // Original chain still self-consistent.
        Assert.NotEqual(h1, h2);
        Assert.NotEqual(h2, h3);
    }

    [Fact]
    public void DifferentKeys_ProduceDifferentHashes()
    {
        var other = Encoding.UTF8.GetBytes("a-different-32-byte-key-000000001");
        var a = AuditLogger.ComputeHash(Key,   Genesis, Utc, "a", "Admin", "e", null, null, null, "{}");
        var b = AuditLogger.ComputeHash(other, Genesis, Utc, "a", "Admin", "e", null, null, null, "{}");
        Assert.NotEqual(a, b);
    }
}
