namespace Servicedesk.Infrastructure.Mail.Graph;

/// Thin abstraction over Microsoft Graph mail queries. Lets the polling
/// service be unit-tested without hitting real Graph, and isolates SDK
/// types from the rest of the app.
public interface IGraphMailClient
{
    /// Fetches the next page in a delta-query chain for a mailbox folder.
    /// <paramref name="folderId"/> is the Graph folder ID selected per queue.
    /// When <paramref name="deltaLink"/> is null the client performs the
    /// initial delta query. The returned <see cref="GraphDeltaPage.DeltaLink"/>
    /// should be persisted and passed back on the next call.
    Task<GraphDeltaPage> ListInboxDeltaAsync(
        string mailbox,
        string folderId,
        string? deltaLink,
        int maxPageSize,
        CancellationToken ct);

    /// Acquires a token + performs a trivial metadata call to prove the
    /// credentials + permissions are wired correctly. Returns the latency.
    Task<TimeSpan> PingAsync(string mailbox, CancellationToken ct);

    /// Full message fetch for ingest: body, recipients, internet headers.
    Task<GraphFullMessage> FetchMessageAsync(string mailbox, string graphMessageId, CancellationToken ct);

    /// Fetches the raw RFC-822 MIME bytes of a message. Caller owns disposing the stream.
    Task<Stream> FetchRawMessageAsync(string mailbox, string graphMessageId, CancellationToken ct);

    /// Marks the message as read in the mailbox.
    Task MarkAsReadAsync(string mailbox, string graphMessageId, CancellationToken ct);

    /// Moves the message to a destination folder by folder id. Graph assigns
    /// a new id to the relocated copy, but callers don't need it: our
    /// finalizer runs <em>after</em> all attachments are already persisted to
    /// local blob-storage, so the old (now-invalid) id is irrelevant.
    Task MoveAsync(string mailbox, string graphMessageId, string destinationFolderId, CancellationToken ct);

    /// Returns the folder id for <paramref name="folderName"/> under the mailbox root,
    /// creating it if missing. Safe to call repeatedly — idempotent.
    Task<string> EnsureFolderAsync(string mailbox, string folderName, CancellationToken ct);

    /// Streams the raw bytes of a single file-attachment on a message. Caller owns disposing.
    Task<Stream> FetchAttachmentBytesAsync(
        string mailbox,
        string graphMessageId,
        string graphAttachmentId,
        CancellationToken ct);

    /// Lists all top-level mail folders in the mailbox. Used by the admin UI
    /// to populate the inbound-folder dropdown per queue.
    Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string mailbox, CancellationToken ct);

    /// Sends a new mail from <paramref name="fromMailbox"/>. Uses the
    /// draft-then-send pattern so the internet-message-id assigned by Graph
    /// can be captured before send — that id is what we persist on the
    /// outbound <c>mail_messages</c> row so inbound replies matching
    /// In-Reply-To / References resolve back to the same ticket.
    Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage message, CancellationToken ct);
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

public sealed record GraphRecipient(string Address, string Name);

public sealed record GraphFullMessage(
    string Id,
    string InternetMessageId,
    string? InReplyTo,
    string? References,
    string Subject,
    GraphRecipient From,
    IReadOnlyList<GraphRecipient> To,
    IReadOnlyList<GraphRecipient> Cc,
    IReadOnlyList<GraphRecipient> Bcc,
    string? BodyHtml,
    string? BodyText,
    DateTimeOffset ReceivedUtc,
    string? AutoSubmitted,
    IReadOnlyList<GraphAttachmentInfo> Attachments);

/// Metadata describing a single file-attachment on a Graph message. Bytes are
/// downloaded asynchronously later via <see cref="IGraphMailClient.FetchAttachmentBytesAsync"/>;
/// this record carries only what's needed to enqueue and identify that work.
public sealed record GraphAttachmentInfo(
    string Id,
    string Name,
    string ContentType,
    long Size,
    bool IsInline,
    string? ContentId);

/// Metadata for a mailbox folder, returned by <see cref="IGraphMailClient.ListMailFoldersAsync"/>.
public sealed record GraphMailFolderInfo(
    string Id,
    string DisplayName,
    int TotalItemCount);

/// Outbound-mail payload for <see cref="IGraphMailClient.SendMailAsync"/>.
/// The caller owns subject/body/recipients; this record carries no logic.
/// <see cref="InternetMessageHeaders"/> are custom RFC-5322 headers stamped
/// on the draft. Microsoft Graph only persists names that start with
/// <c>x-</c>, so the trigger send-path uses <c>X-Auto-Submitted</c> rather
/// than the bare <c>Auto-Submitted</c> name.
public sealed record GraphOutboundMessage(
    string FromMailbox,
    string Subject,
    string BodyHtml,
    IReadOnlyList<GraphRecipient> To,
    IReadOnlyList<GraphRecipient> Cc,
    IReadOnlyList<GraphRecipient> Bcc,
    IReadOnlyList<GraphRecipient> ReplyTo,
    IReadOnlyList<GraphOutboundAttachment>? Attachments = null,
    IReadOnlyList<GraphOutboundHeader>? InternetMessageHeaders = null);

/// One custom RFC-5322 header set on an outbound message.
public sealed record GraphOutboundHeader(string Name, string Value);

/// One file-attachment to ship with an outbound mail. Bytes are passed
/// in-memory because Graph's <c>fileAttachment</c> resource embeds the bytes
/// as base64 in the draft-create body. <see cref="ContentId"/> is set for
/// inline images (referenced from the body via <c>cid:{ContentId}</c>) and
/// must be unique within the message; left null for plain attachments.
public sealed record GraphOutboundAttachment(
    string FileName,
    string ContentType,
    byte[] Bytes,
    bool IsInline,
    string? ContentId);

/// Result of a successful send. InternetMessageId is the RFC-5322 id the
/// recipient will see in <c>In-Reply-To</c> on their reply — we persist it
/// so the existing threading lookup can match replies back to this mail.
public sealed record GraphSentMailResult(
    string InternetMessageId,
    DateTimeOffset SentUtc);
