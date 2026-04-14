namespace Servicedesk.Infrastructure.Mail.Graph;

/// Thin abstraction over Microsoft Graph mail queries. Lets the polling
/// service be unit-tested without hitting real Graph, and isolates SDK
/// types from the rest of the app.
public interface IGraphMailClient
{
    /// Fetches the next page in a delta-query chain for a mailbox's Inbox.
    /// When <paramref name="deltaLink"/> is null the client performs the
    /// initial delta query. The returned <see cref="GraphDeltaPage.DeltaLink"/>
    /// should be persisted and passed back on the next call.
    Task<GraphDeltaPage> ListInboxDeltaAsync(
        string mailbox,
        string? deltaLink,
        int maxPageSize,
        CancellationToken ct);

    /// Acquires a token + performs a trivial metadata call to prove the
    /// credentials + permissions are wired correctly. Returns the latency.
    Task<TimeSpan> PingAsync(string mailbox, CancellationToken ct);
}

public sealed record GraphMailSummary(
    string Id,
    string? InternetMessageId,
    string? Subject,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? ReceivedUtc);

public sealed record GraphDeltaPage(
    IReadOnlyList<GraphMailSummary> Messages,
    string? DeltaLink);
