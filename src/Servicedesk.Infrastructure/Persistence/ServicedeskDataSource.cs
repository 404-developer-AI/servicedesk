using Npgsql;

namespace Servicedesk.Infrastructure.Persistence;

/// The one place an NpgsqlDataSource is built — the app's DI singleton and
/// the database test fixture both go through here, so tests run the same
/// type mapping as production.
public static class ServicedeskDataSource
{
    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
#pragma warning disable NPG9001 // experimental Npgsql type-resolver API
        builder.AddTypeInfoResolverFactory(new LegacyDateAndTimeResolverFactory());
#pragma warning restore NPG9001
        // Npgsql 9+ also traces every physical connection open (a CONNECT
        // activity). PerfInstrumentation only wants command activities —
        // keep Diagnose's stack-capture budget for those, as before.
        builder.ConfigureTracing(o => o.EnablePhysicalOpenTracing(false));
        return builder.Build();
    }
}
