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
///
/// Concurrency note — the counter lives in an <see cref="AsyncLocal{T}"/>.
/// Reads of the value see whatever the current ExecutionContext carries;
/// a write triggers a copy-on-write so the new value flows down to
/// nested awaits but is invisible to siblings or the caller. That keeps
/// the chain-cap correct for the synchronous re-entry pattern this guard
/// was designed for: an action handler whose side-effect re-enters the
/// evaluator stays in the same flow and sees an incremented depth.
///
/// What the guard does NOT prevent: a caller that fans out multiple
/// evaluator passes in parallel via <c>Task.WhenAll(EvaluateAsync(...),
/// EvaluateAsync(...))</c>. Both branches inherit the parent's depth at
/// the start (the AsyncLocal is NOT reset to zero per branch), then
/// increment within their own copy-on-write context — so each branch
/// burns its own cap budget independently of the other. If a future
/// caller wants a per-mutation cap that spans parallel branches, this
/// guard is the wrong primitive; a scoped counter +
/// <see cref="System.Threading.Interlocked"/> would match that
/// semantic. As of v0.0.24 every evaluator entry-point runs
/// sequentially in a single request thread, so AsyncLocal is sufficient.
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
