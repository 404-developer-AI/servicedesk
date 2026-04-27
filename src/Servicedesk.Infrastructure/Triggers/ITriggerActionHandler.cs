using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers;

/// One handler per action <c>kind</c> (<c>set_queue</c>,
/// <c>set_priority</c>, <c>send_mail</c>, …). Block 2 ships only the
/// interface and the dispatcher; concrete handlers land in Block 3 and
/// register themselves through DI as <see cref="ITriggerActionHandler"/>.
/// The dispatcher resolves all registrations and routes by
/// <see cref="Kind"/>; an unregistered kind produces a
/// <see cref="TriggerActionResult.NoHandler"/> outcome which logs as
/// "failed" in <c>trigger_runs</c> rather than silently succeeding.
public interface ITriggerActionHandler
{
    /// The <c>kind</c> string this handler claims, matching the
    /// <c>actions[].kind</c> values in trigger JSONB. Match is
    /// case-sensitive (snake_case is the convention).
    string Kind { get; }

    /// Applies the action and returns a small descriptor of what changed.
    /// Returning <see cref="TriggerActionResult.NoOp"/> is fine when the
    /// ticket is already in the desired state — idempotency is part of
    /// the handler contract (TRIGGERS.md §5).
    Task<TriggerActionResult> ApplyAsync(
        JsonElement actionJson,
        TriggerEvaluationContext ctx,
        CancellationToken ct);
}

/// Outcome of one action-handler call. <see cref="ChangeSummary"/> is
/// surfaced through <c>trigger_runs.applied_changes</c> and the audit
/// payload, so it should be small + JSON-serialisable.
public sealed record TriggerActionResult(
    TriggerActionStatus Status,
    string Kind,
    object? ChangeSummary = null,
    string? FailureReason = null)
{
    public static TriggerActionResult Applied(string kind, object? summary = null)
        => new(TriggerActionStatus.Applied, kind, summary);

    public static TriggerActionResult NoOp(string kind, object? summary = null)
        => new(TriggerActionStatus.NoOp, kind, summary);

    public static TriggerActionResult Failed(string kind, string reason)
        => new(TriggerActionStatus.Failed, kind, FailureReason: reason);

    public static TriggerActionResult NoHandler(string kind)
        => new(TriggerActionStatus.NoHandler, kind, FailureReason: $"No handler registered for action kind '{kind}'.");
}

public enum TriggerActionStatus
{
    Applied,
    NoOp,
    Failed,
    NoHandler,
}
