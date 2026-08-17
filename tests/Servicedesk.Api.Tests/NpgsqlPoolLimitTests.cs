using Microsoft.Extensions.Configuration;
using Npgsql;
using Servicedesk.Infrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.99 — the app pins its Npgsql pool below the Postgres connection
/// budget so a full pool degrades to a wait instead of a 53300 FATAL.
public sealed class NpgsqlPoolLimitTests
{
    private const string Base = "Host=localhost;Database=sd;Username=u;Password=p";

    [Fact]
    public void Default_pool_size_is_applied_when_connection_string_has_none()
    {
        var result = new NpgsqlConnectionStringBuilder(
            DependencyInjection.ApplyPoolLimits(Base, EmptyConfig()));

        Assert.Equal(DependencyInjection.DefaultMaxPoolSize, result.MaxPoolSize);
    }

    [Fact]
    public void Explicit_pool_size_in_connection_string_wins()
    {
        var result = new NpgsqlConnectionStringBuilder(
            DependencyInjection.ApplyPoolLimits(Base + ";Maximum Pool Size=33", Config(("Database:MaxPoolSize", "50"))));

        Assert.Equal(33, result.MaxPoolSize);
    }

    [Fact]
    public void Configuration_override_is_honoured_and_clamped()
    {
        var overridden = new NpgsqlConnectionStringBuilder(
            DependencyInjection.ApplyPoolLimits(Base, Config(("Database:MaxPoolSize", "120"))));
        Assert.Equal(120, overridden.MaxPoolSize);

        var clamped = new NpgsqlConnectionStringBuilder(
            DependencyInjection.ApplyPoolLimits(Base, Config(("Database:MaxPoolSize", "5000"))));
        Assert.Equal(1000, clamped.MaxPoolSize);

        var ignored = new NpgsqlConnectionStringBuilder(
            DependencyInjection.ApplyPoolLimits(Base, Config(("Database:MaxPoolSize", "0"))));
        Assert.Equal(DependencyInjection.DefaultMaxPoolSize, ignored.MaxPoolSize);
    }

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
}
