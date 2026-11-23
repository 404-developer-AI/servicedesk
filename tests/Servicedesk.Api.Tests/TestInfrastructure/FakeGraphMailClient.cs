using System.Text;
using Servicedesk.Infrastructure.Mail.Graph;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// The one <see cref="IGraphMailClient"/> fake: serves a configured message
/// and attachment bytes, records sent mail, and can simulate Graph failures.
/// Delta paging is never part of a unit test and throws.
public sealed class FakeGraphMailClient : IGraphMailClient
{
    /// What <see cref="FetchMessageAsync"/> returns.
    public GraphFullMessage? Message { get; set; }

    public byte[] RawMessage { get; set; } = Encoding.UTF8.GetBytes("raw");
    public byte[] AttachmentBytes { get; set; } = Array.Empty<byte>();
    public bool ThrowOnAttachmentFetch { get; set; }

    public List<GraphOutboundMessage> Sent { get; } = new();
    public GraphOutboundMessage? LastMessage => Sent.Count == 0 ? null : Sent[^1];
    public bool ThrowOnSend { get; set; }

    /// Graph refuses the reply draft (original gone) → plain send with a fallback reason.
    public bool FailReply { get; set; }

    public Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage message, CancellationToken ct)
    {
        if (ThrowOnSend) throw new InvalidOperationException("Graph boom");
        Sent.Add(message);
        var isReply = message.ReplyToGraphMessageId is not null;
        return Task.FromResult(new GraphSentMailResult("msg-id@graph", DateTimeOffset.UtcNow)
        {
            SentAsReply = isReply && !FailReply,
            ReplyFallbackReason = isReply && FailReply ? "createReply failed: status=404" : null,
        });
    }

    public Task<GraphFullMessage> FetchMessageAsync(string mailbox, string graphMessageId, CancellationToken ct)
        => Task.FromResult(Message ?? throw new InvalidOperationException("FakeGraphMailClient.Message is not set."));

    public Task<Stream> FetchRawMessageAsync(string mailbox, string graphMessageId, CancellationToken ct)
        => Task.FromResult<Stream>(new MemoryStream(RawMessage, writable: false));

    public Task<Stream> FetchAttachmentBytesAsync(string mailbox, string graphMessageId, string graphAttachmentId, CancellationToken ct)
    {
        if (ThrowOnAttachmentFetch) throw new InvalidOperationException("boom");
        return Task.FromResult<Stream>(new MemoryStream(AttachmentBytes, writable: false));
    }

    public Task<GraphDeltaPage> ListInboxDeltaAsync(string mailbox, string folderId, string? deltaLink, int batchSize, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string mailbox, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<GraphMailFolderInfo>>(Array.Empty<GraphMailFolderInfo>());

    public Task<TimeSpan> PingAsync(string mailbox, CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
    public Task MarkAsReadAsync(string mailbox, string graphMessageId, CancellationToken ct) => Task.CompletedTask;
    public Task MoveAsync(string mailbox, string graphMessageId, string destinationFolderId, CancellationToken ct) => Task.CompletedTask;
    public Task<string> EnsureFolderAsync(string mailbox, string folderName, CancellationToken ct) => Task.FromResult("f");
}
