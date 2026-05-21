namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Per-ticket resolver result, shared by the dry-run worker (insert
/// path) and the recheck service (update path). The resolver itself
/// only knows about Zammad + the mapping dictionary; persistence is
/// caller-side.
public sealed record ZammadTicketResolveResult(
    long ZammadTicketId,
    string? ZammadTicketNumber,
    string? ZammadTicketTitle,
    string Result,
    IReadOnlyList<string> UnresolvedReasons,
    IReadOnlyDictionary<string, object?> Mapping);

/// Resolver for a single Zammad ticket against the install's current
/// mapping dictionary. Used by:
/// <list type="bullet">
/// <item><see cref="ZammadDryRunWorker"/> on the original dry-run pass
/// (one resolve per ticket in the run).</item>
/// <item>The recheck endpoint when an admin has just created a
/// missing contact and wants previously-skipped records re-evaluated
/// without rerunning the entire run.</item>
/// </list>
/// Returns a result with <c>Result = "failed"</c> + a reason when the
/// upstream fetch errors so callers can persist a record and move on
/// instead of bubbling the exception.
public interface IZammadTicketResolver
{
    Task<ZammadTicketResolveResult> ResolveAsync(
        long zammadTicketId,
        ZammadMappingDictionary dict,
        CancellationToken ct);
}
