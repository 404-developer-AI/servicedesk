using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class MailTimelineEnricherTests
{
    private static readonly Guid TicketId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MailId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid InlineId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task Rewrites_cid_references_for_ready_inline_attachments()
    {
        const string html = "<html><body><img src=\"cid:img-001\"/><p>Hello</p></body></html>";
        var enricher = Build(html, new[]
        {
            MakeAttachment(InlineId, contentId: "img-001", state: "Ready"),
        });
        var detail = MakeDetailWithMailReceivedEvent();

        var result = await enricher.EnrichAsync(detail, default);

        var evt = result.Events.Single();
        Assert.NotNull(evt.BodyHtml);
        Assert.Contains($"/api/tickets/{TicketId}/mail/{MailId}/attachments/{InlineId}", evt.BodyHtml);
        Assert.DoesNotContain("cid:img-001", evt.BodyHtml);
    }

    [Fact]
    public async Task Leaves_cid_intact_when_attachment_not_ready()
    {
        const string html = "<img src=\"cid:img-001\"/>";
        var enricher = Build(html, new[]
        {
            MakeAttachment(InlineId, contentId: "img-001", state: "Pending"),
        });
        var detail = MakeDetailWithMailReceivedEvent();

        var result = await enricher.EnrichAsync(detail, default);

        Assert.Contains("cid:img-001", result.Events.Single().BodyHtml);
    }

    [Fact]
    public async Task Other_event_types_are_left_unchanged()
    {
        var enricher = Build("<p/>", Array.Empty<AttachmentRow>());
        var detail = new TicketDetail(
            Ticket: MakeTicket(),
            Body: new TicketBody(TicketId, "body", null),
            Events: new[]
            {
                new TicketEvent(1, TicketId, "Comment", null, null, null, "text", "<p>keep</p>", "{}", false, DateTime.UtcNow, null, null),
            },
            PinnedEvents: Array.Empty<TicketEventPin>());

        var result = await enricher.EnrichAsync(detail, default);

        Assert.Equal("<p>keep</p>", result.Events.Single().BodyHtml);
    }

    private static TicketDetail MakeDetailWithMailReceivedEvent()
    {
        var metadata = JsonSerializer.Serialize(new { mail_message_id = MailId.ToString() });
        return new TicketDetail(
            Ticket: MakeTicket(),
            Body: new TicketBody(TicketId, "", null),
            Events: new[]
            {
                new TicketEvent(1, TicketId, "MailReceived", null, null, "sender",
                    BodyText: "plaintext snippet",
                    BodyHtml: null,
                    MetadataJson: metadata,
                    IsInternal: false,
                    CreatedUtc: DateTime.UtcNow,
                    EditedUtc: null,
                    EditedByUserId: null),
            },
            PinnedEvents: Array.Empty<TicketEventPin>());
    }

    private static Ticket MakeTicket() => new(
        Id: TicketId, Number: 1, Subject: "s",
        RequesterContactId: Guid.NewGuid(), AssigneeUserId: null,
        QueueId: Guid.NewGuid(), StatusId: Guid.NewGuid(), PriorityId: Guid.NewGuid(),
        CategoryId: null, Source: "Mail", ExternalRef: null,
        CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow,
        DueUtc: null, FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null,
        IsDeleted: false);

    private static AttachmentRow MakeAttachment(Guid id, string contentId, string state) => new(
        Id: id, OwnerId: MailId, OwnerKind: "Mail",
        ContentHash: state == "Ready" ? "abc" : null,
        SizeBytes: 1, MimeType: "image/png",
        OriginalFilename: "x.png", IsInline: true, ContentId: contentId,
        ProcessingState: state);

    private static MailTimelineEnricher Build(string html, IReadOnlyList<AttachmentRow> attachments)
    {
        var mailRepo = new StubMailRepo(MailId, bodyHtmlHash: "hash-html");
        var blobs = new StubBlobStore(new Dictionary<string, string> { ["hash-html"] = html });
        var attRepo = new StubAttachmentRepo(attachments);
        return new MailTimelineEnricher(mailRepo, attRepo, blobs, NullLogger<MailTimelineEnricher>.Instance);
    }

    private sealed class StubMailRepo : IMailMessageRepository
    {
        private readonly MailMessageRow _row;
        public StubMailRepo(Guid id, string bodyHtmlHash)
        {
            _row = new MailMessageRow(id, "mid", null, "s", "from@x", "", "box@x",
                DateTime.UtcNow, null, bodyHtmlHash, "", null, null, null, null);
        }
        public Task<MailMessageRow?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<MailMessageRow?>(_row);
        public Task<MailMessageRow?> GetByMessageIdAsync(string messageId, CancellationToken ct) => Task.FromResult<MailMessageRow?>(null);
        public Task<Guid?> FindTicketIdByReferencesAsync(IReadOnlyList<string> ids, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<Guid> InsertAsync(NewMailMessage row, IReadOnlyList<NewMailRecipient> r, IReadOnlyList<NewMailAttachment> a, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task AttachToTicketAsync(Guid mailId, Guid ticketId, long eventId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkMailboxMovedAsync(Guid mailId, DateTime utc, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FinalizeCandidate>> ListReadyForFinalizeAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FinalizeCandidate>>(Array.Empty<FinalizeCandidate>());
        public Task<FinalizeCandidate?> GetIfReadyForFinalizeAsync(Guid mailId, CancellationToken ct)
            => Task.FromResult<FinalizeCandidate?>(null);
        public Task<Guid> InsertOutboundAsync(NewOutboundMailMessage row, IReadOnlyList<NewMailRecipient> r, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task<MailThreadAnchor?> GetLatestThreadAnchorAsync(Guid ticketId, CancellationToken ct) => Task.FromResult<MailThreadAnchor?>(null);
        public Task<IReadOnlyList<MailRecipientRow>> ListRecipientsAsync(Guid mailId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MailRecipientRow>>(Array.Empty<MailRecipientRow>());
    }

    private sealed class StubAttachmentRepo : IAttachmentRepository
    {
        private readonly IReadOnlyList<AttachmentRow> _rows;
        public StubAttachmentRepo(IReadOnlyList<AttachmentRow> rows) => _rows = rows;
        public Task<AttachmentRow?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<AttachmentRow?>(_rows.FirstOrDefault(r => r.Id == id));
        public Task<IReadOnlyList<AttachmentRow>> ListByMailAsync(Guid mailId, CancellationToken ct) => Task.FromResult(_rows);
        public Task<IReadOnlyList<AttachmentRow>> ListByEventAsync(long eventId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AttachmentRow>>(_rows.Where(r => r.EventId == eventId).ToList());
        public Task<bool> MarkReadyAsync(Guid id, string h, long s, string m, CancellationToken ct) => Task.FromResult(true);
        public Task MarkFailedAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<Guid> CreateUploadedAsync(NewUploadedAttachment input, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ReassignToEventAsync(IReadOnlyList<Guid> ids, Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ReassignToMailAsync(IReadOnlyList<AttachmentReassignToMail> assignments, Guid ticketId, Guid mailMessageId, long ticketEventId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubBlobStore : IBlobStore
    {
        private readonly IReadOnlyDictionary<string, string> _files;
        public StubBlobStore(IReadOnlyDictionary<string, string> files) => _files = files;
        public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken ct = default)
            => Task.FromResult<Stream?>(_files.TryGetValue(contentHash, out var c)
                ? new MemoryStream(Encoding.UTF8.GetBytes(c))
                : null);
        public Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeleteAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
    }
}
