using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The suite boots hundreds of in-memory hosts in one process. A disposed host
/// that stays reachable costs ~15 MB and slows every later boot; before this
/// guard the CI runner ran low on memory and the backend job took an hour.
/// Known roots: the rate limiter's process-lifetime timers, through the
/// captured execution context (NoExecutionContextFlowStartupFilter) and
/// through the middleware's next-delegate (UseServicedeskRateLimiter).
public sealed class TestHostLeakTests
{
    [Fact]
    public async Task Disposed_hosts_are_collectable()
    {
        const int hosts = 3;
        var providers = new List<WeakReference>();
        for (var i = 0; i < hosts; i++)
        {
            providers.Add(await BootServeAndDisposeAsync());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The most recent host may stay reachable through process-wide
        // "latest instance" statics (Serilog's Log.Logger, PerfRuntime); every
        // host before it must be gone.
        var alive = providers.Count(p => p.IsAlive);
        Assert.True(alive <= 1, $"{alive} of {hosts} disposed hosts are still reachable");
    }

    private static async Task<WeakReference> BootServeAndDisposeAsync()
    {
        await using var factory = new SecurityBaselineFactory();
        using var client = factory.CreateClient();
        // A rate-limited request, so the limiter's partitions exist too.
        await client.GetAsync("/api/system/version");
        return new WeakReference(factory.Services);
    }
}
