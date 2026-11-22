using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// One booted test host per test class instead of one per test.
///
/// Booting a SecurityBaselineFactory costs ~3 s (host build + first request
/// building the endpoint table); with a host per test that was most of the
/// backend CI time. A class takes this as <c>IClassFixture</c> and calls
/// <see cref="Reset"/> from its constructor — xUnit builds the class per test,
/// and runs the tests of one class sequentially, so every test starts from
/// the shipped settings, no secrets, no users/sessions and an empty audit log.
///
/// <see cref="Base"/> is the plain host. Tests that need extra fakes get a
/// derived host per variant from <see cref="Host"/>; the variant key must
/// identify the configuration completely (it is built once and reused).
/// Derived hosts share the base's fakes (Settings, Sessions, Users, …).
///
/// Not for tests that need different startup configuration (WithConfig — e.g.
/// rate-limit budgets are read once at startup) or that exhaust a rate-limit
/// budget: those keep their own factory.
public sealed class SharedHostFixture : IDisposable
{
    private readonly ConcurrentDictionary<string, WebApplicationFactory<Program>> _hosts = new(StringComparer.Ordinal);

    public SecurityBaselineFactory Base { get; } = new();

    public WebApplicationFactory<Program> Host(string variant, Action<IWebHostBuilder> configure) =>
        _hosts.GetOrAdd(variant, _ => Base.WithWebHostBuilder(configure));

    /// A host with a data source that points nowhere. Handlers resolve their
    /// repositories before running, so a request that gets past a gate needs
    /// one; a gated request must answer before any query, and one that does
    /// reach the database fails fast.
    public WebApplicationFactory<Program> UnreachableDb =>
        Host("unreachable-db", builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        }));

    public void Reset() => Base.ResetState();

    // Disposing the base disposes every derived host too.
    public void Dispose() => Base.Dispose();
}
