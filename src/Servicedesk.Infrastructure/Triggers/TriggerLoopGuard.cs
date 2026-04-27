namespace Servicedesk.Infrastructure.Triggers;

/// Tracks how deep we are in a trigger-evaluation chain on the current
/// async-flow. A trigger that mutates a ticket whose mutation re-enters
/// <see cref="ITriggerService.EvaluateAsync"/> would otherwise be able to
/// loop indefinitely; the guard caps that chain at the
/// <c>Triggers.MaxChainPerMutation</c> setting.
///
/// Use pattern: the evaluator opens a <see cref="Scope"/> at the top of
/// each pass; the scope decrements the counter on dispose. Action-handlers
/// running inside the evaluator see <see cref="Depth"/> &gt; 0 and any
/// re-entry from their side-effects is gated by the same counter.
public sealed class TriggerLoopGuard
{
    private static readonly AsyncLocal<int> _depth = new();

    public int Depth => _depth.Value;

    public Scope Enter() => new(this);

    public sealed class Scope : IDisposable
    {
        private readonly TriggerLoopGuard _owner;
        private bool _disposed;
        internal Scope(TriggerLoopGuard owner)
        {
            _owner = owner;
            _depth.Value = _depth.Value + 1;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _depth.Value = Math.Max(0, _depth.Value - 1);
        }
    }
}
