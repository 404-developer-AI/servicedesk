using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Servicedesk.Infrastructure.DataProtection;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class DataProtectionKeyringTests
{
    private static NpgsqlDataSource StubDataSource() =>
        new NpgsqlDataSourceBuilder("Host=localhost;Database=x;Username=x;Password=x").Build();

    [Fact]
    public void PostgresXmlRepository_Rejects_MasterKey_With_Wrong_Length()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PostgresXmlRepository(StubDataSource(), new byte[16], NullLogger<PostgresXmlRepository>.Instance));
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void AddServicedeskDataProtection_Throws_When_MasterKey_Missing()
    {
        var services = BuildServicesWith(masterKey: null);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<PostgresXmlRepository>());
        Assert.Contains("DataProtection:MasterKey", ex.Message);
    }

    [Fact]
    public void AddServicedeskDataProtection_Throws_When_MasterKey_Not_Base64()
    {
        var services = BuildServicesWith(masterKey: "not-base64-!!!");
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<PostgresXmlRepository>());
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void AddServicedeskDataProtection_Throws_When_MasterKey_Wrong_Length()
    {
        // 16-byte key base64-encoded — valid base64 but wrong length for AES-256.
        var services = BuildServicesWith(masterKey: Convert.ToBase64String(new byte[16]));
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<PostgresXmlRepository>());
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void AddServicedeskDataProtection_Succeeds_With_Valid_MasterKey()
    {
        var services = BuildServicesWith(masterKey: Convert.ToBase64String(new byte[32]));
        using var provider = services.BuildServiceProvider();

        var repo = provider.GetRequiredService<PostgresXmlRepository>();
        Assert.NotNull(repo);
    }

    private static IServiceCollection BuildServicesWith(string? masterKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:MasterKey"] = masterKey,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NpgsqlDataSource>(_ => StubDataSource());
        services.AddServicedeskDataProtection(config);
        return services;
    }
}
