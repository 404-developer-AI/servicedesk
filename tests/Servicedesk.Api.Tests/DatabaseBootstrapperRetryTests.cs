using System.Net.Sockets;
using Npgsql;
using Servicedesk.Infrastructure.Persistence;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class DatabaseBootstrapperRetryTests
{
    [Fact]
    public void IsTransient_SocketException_ReturnsTrue()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);
        Assert.True(DatabaseBootstrapper.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_TimeoutException_ReturnsTrue()
    {
        Assert.True(DatabaseBootstrapper.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void IsTransient_NpgsqlWithSocketInner_ReturnsTrue()
    {
        var inner = new SocketException((int)SocketError.HostUnreachable);
        var ex = new NpgsqlException("connection refused", inner);
        Assert.True(DatabaseBootstrapper.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_InvalidOperation_ReturnsFalse()
    {
        // Schema errors (table already exists with wrong columns etc.) are not
        // transient — retrying wouldn't help and would mask the real bug.
        Assert.False(DatabaseBootstrapper.IsTransient(new InvalidOperationException()));
    }

    [Fact]
    public void IsTransient_ArgumentNull_ReturnsFalse()
    {
        Assert.False(DatabaseBootstrapper.IsTransient(new ArgumentNullException("foo")));
    }
}
