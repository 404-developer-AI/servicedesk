using System.Text.Json;

namespace Servicedesk.Infrastructure.Triggers;

/// Dry-run counterpart of <see cref="ITriggerActionHandler"/> for v0.0.24
/// Blok 7. Each registered <see cref="Kind"/> declares "what this action
/// would do on this ticket" without mutating state, sending mail, or
/// touching <c>ticket_events</c>. Used by the admin test-runner so admins
/// can validate a trigger against a real ticket before flipping it on.
///
/// Previewers may issue read-only lookups (resolve queue/priority names,
/// render template values) but must never UPDATE/INSERT or call any
/// side-effecting infrastructure (<see cref="Mail.Graph.IGraphMailClient"/>,
/// notification hubs, …). The dispatcher trusts that contract — a
/// previewer that mutates would silently leak through the test-runner.
public interface ITriggerActionPreviewer
{
    /// Action <c>kind</c> string this previewer claims; matches the
    /// concrete <see cref="ITriggerActionHandler.Kind"/> exactly.
    string Kind { get; }

    Task<TriggerActionPreviewResult> PreviewAsync(
        JsonElement actionJson,
        TriggerEvaluationContext ctx,
        CancellationToken ct);
}

public enum TriggerActionPreviewStatus
{
    /// The action would apply: a real run produces a non-NoOp change.
    WouldApply,
    /// The action would short-circuit (already at target, dedup-window,
    /// empty recipient list, …) and produce no observable change.
    WouldNoOp,
    /// Action JSON or context is invalid (missing field, unparseable
    /// duration, unknown recipient spec, …). Mirrors the runtime
    /// validation a real run would hit.
    Failed,
    /// No previewer is registered for this action kind. Maps to the
    /// real-run <c>NoHandler</c> outcome so the UI shows the same gap.
    NoHandler,
}

public sealed record TriggerActionPreviewResult(
    TriggerActionPreviewStatus Status,
    string Kind,
    object? Summary = null,
    string? FailureReason = null)
{
    public static TriggerActionPreviewResult WouldApply(string kind, object? summary = null)
        => new(TriggerActionPreviewStatus.WouldApply, kind, summary);

    public static TriggerActionPreviewResult WouldNoOp(string kind, object? summary = null)
        => new(TriggerActionPreviewStatus.WouldNoOp, kind, summary);

    public static TriggerActionPreviewResult Failed(string kind, string reason)
        => new(TriggerActionPreviewStatus.Failed, kind, FailureReason: reason);

    public static TriggerActionPreviewResult NoHandler(string kind)
        => new(TriggerActionPreviewStatus.NoHandler, kind, FailureReason: $"No previewer registered for action kind '{kind}'.");
}
