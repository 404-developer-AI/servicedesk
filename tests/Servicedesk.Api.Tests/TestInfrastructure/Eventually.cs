using Xunit;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// Waits for an asynchronous side effect by polling a condition up to a
/// deadline — instead of a fixed sleep that is either too short on a slow CI
/// runner or wasted time on a fast one.
public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), $"Not reached within the deadline: {because}");
    }
}
