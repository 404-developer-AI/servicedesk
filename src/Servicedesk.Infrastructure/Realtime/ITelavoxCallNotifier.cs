namespace Servicedesk.Infrastructure.Realtime;

/// Per-agent SignalR push for Telavox call-popup events (v0.0.34 commit C).
/// The polling worker invokes this whenever the transition module decides
/// the configured trigger (default: RINGING → ANSWERED) fired on a linked
/// agent's extension. Group key is <c>telavox-user:{userId}</c> so an admin
/// who happens to know the hub URL can't subscribe to another user's
/// call-stream.
///
/// <para>
/// The payload is intentionally narrow — it carries only the upstream
/// callId, current state, raw From/To numbers and a server-side
/// occurred-utc timestamp. Anything richer (contact lookup, ticket list,
/// company hints) is computed by the SPA via its existing search /
/// contacts endpoints; the push channel never carries that data so it
/// stays auth-safe and small on the wire.
/// </para>
public interface ITelavoxCallNotifier
{
    Task NotifyCallAsync(Guid userId, TelavoxCallEventPayload payload, CancellationToken ct);
}

/// Wire-format for a call-popup push. The state mirrors the upstream
/// Telavox vocabulary (uppercase, e.g. "RINGING" / "ANSWERED") so the SPA
/// can decide on its own whether to show a ringing toast or a
/// connected-call card.
public sealed record TelavoxCallEventPayload(
    string CallId,
    string State,
    string? FromNumber,
    string? ToNumber,
    DateTimeOffset? StartUtc,
    DateTimeOffset OccurredUtc);

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullTelavoxCallNotifier : ITelavoxCallNotifier
{
    public Task NotifyCallAsync(Guid userId, TelavoxCallEventPayload payload, CancellationToken ct)
        => Task.CompletedTask;
}
