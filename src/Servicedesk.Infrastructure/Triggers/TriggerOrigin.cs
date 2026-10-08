namespace Servicedesk.Infrastructure.Triggers;

/// v0.1.32 — the agent whose own action is causing the current trigger
/// pass. Request-path evaluations (an agent saves a note, sends a mail,
/// changes a field) run inside that agent's request; the call site opens
/// a scope here so every system event a trigger writes in that pass is
/// stamped <c>on_behalf_of</c> = the agent (see TriggerEventMetadata).
/// Scheduler / time-trigger passes, inbound mail and the portal never open
/// a scope, so their events carry no agent. Insights Workflow uses this to
/// attribute a trigger-driven move into Pending to the agent behind it.
public static class TriggerOrigin
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public static Guid? OnBehalfOfUserId => Current.Value;

    public static IDisposable Agent(Guid userId)
    {
        var previous = Current.Value;
        Current.Value = userId == Guid.Empty ? previous : userId;
        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Current.Value = previous;
        }
    }
}
