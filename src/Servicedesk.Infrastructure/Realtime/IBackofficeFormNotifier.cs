namespace Servicedesk.Infrastructure.Realtime;

/// v0.1.54 — tells every open session of a Back office forms user that the
/// work list changed (a form was submitted, claimed, handled, edited…), so the
/// list and the red nav dot refresh without polling. The push carries no
/// payload: the client re-reads the flag-gated endpoints, so a session whose
/// flag was revoked after connecting learns nothing from it.
public interface IBackofficeFormNotifier
{
    Task BackofficeFormsChangedAsync(CancellationToken ct);
}

/// No-op fallback used when SignalR is not wired (unit tests, offline jobs).
public sealed class NullBackofficeFormNotifier : IBackofficeFormNotifier
{
    public Task BackofficeFormsChangedAsync(CancellationToken ct) => Task.CompletedTask;
}
