using Servicedesk.Domain.Sla;
using Servicedesk.Infrastructure.Sla;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.101 — the SLA engine resolves policies from an in-process snapshot;
/// the selection must match the SQL it replaced (queue-specific policy for
/// the priority beats the queue-less fallback, nothing else matches).
public sealed class SlaPolicySelectionTests
{
    private static readonly Guid QueueA = Guid.NewGuid();
    private static readonly Guid QueueB = Guid.NewGuid();
    private static readonly Guid High = Guid.NewGuid();
    private static readonly Guid Low = Guid.NewGuid();
    private static readonly Guid Schema = Guid.NewGuid();

    private static SlaPolicy P(Guid? queue, Guid priority) =>
        new(Guid.NewGuid(), queue, priority, Schema, 60, 480, true);

    [Fact]
    public void Queue_specific_policy_wins_over_fallback_regardless_of_order()
    {
        var fallback = P(null, High);
        var specific = P(QueueA, High);
        Assert.Same(specific, SlaRepository.SelectPolicy(new[] { fallback, specific }, QueueA, High));
        Assert.Same(specific, SlaRepository.SelectPolicy(new[] { specific, fallback }, QueueA, High));
    }

    [Fact]
    public void Falls_back_to_queue_less_policy_for_other_queues()
    {
        var fallback = P(null, High);
        var specific = P(QueueA, High);
        Assert.Same(fallback, SlaRepository.SelectPolicy(new[] { specific, fallback }, QueueB, High));
        Assert.Same(fallback, SlaRepository.SelectPolicy(new[] { specific, fallback }, null, High));
    }

    [Fact]
    public void Other_priorities_and_other_queues_never_match()
    {
        var onlyLowA = P(QueueA, Low);
        Assert.Null(SlaRepository.SelectPolicy(new[] { onlyLowA }, QueueA, High));
        Assert.Null(SlaRepository.SelectPolicy(new[] { onlyLowA }, QueueB, Low));
        Assert.Null(SlaRepository.SelectPolicy(Array.Empty<SlaPolicy>(), QueueA, High));
    }
}
