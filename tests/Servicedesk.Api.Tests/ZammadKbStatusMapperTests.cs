using Servicedesk.Infrastructure.Integrations.Zammad;
using Xunit;

namespace Servicedesk.Api.Tests;

public class ZammadKbStatusMapperTests
{
    [Fact]
    public void All_null_returns_Draft()
    {
        Assert.Equal("Draft", ZammadKbStatusMapper.Map(null, null, null));
    }

    [Fact]
    public void Internal_only_returns_Internal()
    {
        Assert.Equal("Internal", ZammadKbStatusMapper.Map(DateTimeOffset.UtcNow, null, null));
    }

    [Fact]
    public void Published_takes_priority_over_internal()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Published", ZammadKbStatusMapper.Map(now.AddDays(-2), now, null));
    }

    [Fact]
    public void Archived_takes_priority_over_published_and_internal()
    {
        // Zammad keeps the older status timestamps populated after a
        // status flip (archived rows still carry their published_at).
        // The mapper must surface the latest bucket, not the earliest.
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Archived",
            ZammadKbStatusMapper.Map(now.AddDays(-3), now.AddDays(-1), now));
    }

    [Fact]
    public void Archived_alone_returns_Archived()
    {
        Assert.Equal("Archived",
            ZammadKbStatusMapper.Map(null, null, DateTimeOffset.UtcNow));
    }
}
