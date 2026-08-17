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
    public async Task Replaces_not_ready_attachment_cid_with_placeholder()
    {
        // A Pending cid gets the placeholder instead of a broken image (and a
        // CSP console violation — cid: is not an allowed img-src scheme). It
        // self-heals: enrichment runs at read time, so the next load after the
        // attachment worker finishes rewrites the real URL.
        const string html = "<img src=\"cid:img-001\"/>";
        var enricher = Build(html, new[]
        {
            MakeAttachment(InlineId, contentId: "img-001", state: "Pending"),
        });
        var detail = MakeDetailWithMailReceivedEvent();

        var result = await enricher.EnrichAsync(detail, default);

        var body = result.Events.Single().BodyHtml;
        Assert.DoesNotContain("cid:img-001", body);
        Assert.Contains("src=\"data:image/svg+xml;base64,", body);
    }

    [Fact]
    public async Task Replaces_unknown_cid_with_placeholder_even_without_any_attachments()
    {
        // The mail references inline images that never made it into the ingest
        // (typical for forwarded/quoted threads). No attachment rows at all —
        // the rewrite must still run and swap in the placeholder.
        const string html = "<img src=\"cid:ii_19f034a6552d53f12686\"/><img src='cid:other'/>";
        var enricher = Build(html, Array.Empty<AttachmentRow>());
        var detail = MakeDetailWithMailReceivedEvent();

        var result = await enricher.EnrichAsync(detail, default);

        var body = result.Events.Single().BodyHtml;
        Assert.DoesNotContain("cid:", body);
        Assert.Equal(2, CountOccurrences(body!, "data:image/svg+xml;base64,"));
    }

    [Fact]
    public async Task Leaves_cid_text_outside_src_attributes_untouched()
    {
        // A literal "cid:…" in visible mail text is not an image source and
        // must stay text — swapping in a data: URI would garble the body.
        const string html = "<p>the part cid:some-part was missing</p>";
        var enricher = Build(html, Array.Empty<AttachmentRow>());
        var detail = MakeDetailWithMailReceivedEvent();

        var result = await enricher.EnrichAsync(detail, default);

        Assert.Contains("cid:some-part", result.Events.Single().BodyHtml);
        Assert.DoesNotContain("data:image/svg+xml", result.Events.Single().BodyHtml);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
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

    [Fact]
    public async Task Injects_from_and_recipients_into_mail_received_metadata()
    {
        var recipients = new[]
        {
            new MailRecipientRow("to", "to@x", "To Person"),
            new MailRecipientRow("cc", "cc@x", ""),
            new MailRecipientRow("bcc", "bcc@x", ""),
        };
        var enricher = Build("<p>hi</p>", Array.Empty<AttachmentRow>(), recipients);
        var detail = MakeDetailWithMailReceivedEvent();

        var result = await enricher.EnrichAsync(detail, default);

        using var doc = JsonDocument.Parse(result.Events.Single().MetadataJson);
        var root = doc.RootElement;
        Assert.Equal("from@x", root.GetProperty("from").GetString());
        Assert.Equal("to@x", root.GetProperty("to")[0].GetProperty("address").GetString());
        Assert.Equal("To Person", root.GetProperty("to")[0].GetProperty("name").GetString());
        Assert.Equal("cc@x", root.GetProperty("cc")[0].GetProperty("address").GetString());
        Assert.Equal("bcc@x", root.GetProperty("bcc")[0].GetProperty("address").GetString());
    }

    [Fact]
    public async Task Omits_bcc_key_when_no_blind_recipients()
    {
        var recipients = new[] { new MailRecipientRow("to", "to@x", "") };
        var enricher = Build("<p>hi</p>", Array.Empty<AttachmentRow>(), recipients);
        var detail = MakeDetailWithMailReceivedEvent();

        var result = await enricher.EnrichAsync(detail, default);

        using var doc = JsonDocument.Parse(result.Events.Single().MetadataJson);
        Assert.False(doc.RootElement.TryGetProperty("bcc", out _));
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

    private static MailTimelineEnricher Build(
        string html, IReadOnlyList<AttachmentRow> attachments,
        IReadOnlyList<MailRecipientRow>? recipients = null,
        StubLookup? lookup = null)
    {
        lookup ??= new StubLookup(MailId, bodyHtmlHash: "hash-html", attachments, recipients);
        var blobs = new StubBlobStore(new Dictionary<string, string> { ["hash-html"] = html });
        return new MailTimelineEnricher(lookup, blobs, NullLogger<MailTimelineEnricher>.Instance);
    }

    /// v0.0.101 — the enricher must issue ONE batched lookup regardless of
    /// how many mail / note / sent-mail events the timeline has (it used to
    /// be 2–3 queries per inbound mail and 1–3 per note/comment/sent mail).
    [Fact]
    public async Task Batch_lookup_is_called_once_regardless_of_timeline_length()
    {
        var lookup = new StubLookup(MailId, bodyHtmlHash: "hash-html", Array.Empty<AttachmentRow>(), null);
        var enricher = Build("<p>hi</p>", Array.Empty<AttachmentRow>(), lookup: lookup);

        var mailMeta = JsonSerializer.Serialize(new { mail_message_id = MailId.ToString() });
        var events = new List<TicketEvent>();
        for (var i = 1; i <= 40; i++)
        {
            var type = (i % 4) switch { 0 => "MailReceived", 1 => "Note", 2 => "MailSent", _ => "Comment" };
            events.Add(new TicketEvent(i, TicketId, type, null, null, "x", "t", "<p/>",
                type == "MailReceived" ? mailMeta : "{}", false, DateTime.UtcNow, null, null));
        }
        var detail = new TicketDetail(MakeTicket(), new TicketBody(TicketId, "", null), events, Array.Empty<TicketEventPin>());

        var result = await enricher.EnrichAsync(detail, default);

        Assert.Equal(1, lookup.LoadCalls);
        Assert.Equal(40, result.Events.Count);
        // The id sets handed to the lookup are exactly the ones the timeline needs.
        Assert.Equal(10, lookup.LastMailIds!.Count);        // MailReceived events (all point at MailId)
        Assert.Equal(30, lookup.LastEventIds!.Count);       // Note + MailSent + Comment
        Assert.Equal(10, lookup.LastSentEventIds!.Count);   // MailSent only
    }

    [Fact]
    public async Task Timeline_without_enrichable_events_skips_the_lookup_entirely()
    {
        var lookup = new StubLookup(MailId, bodyHtmlHash: "hash-html", Array.Empty<AttachmentRow>(), null);
        var enricher = Build("<p>hi</p>", Array.Empty<AttachmentRow>(), lookup: lookup);
        var detail = new TicketDetail(MakeTicket(), new TicketBody(TicketId, "", null), new[]
        {
            new TicketEvent(1, TicketId, "StatusChange", null, null, null, "t", null, "{}", false, DateTime.UtcNow, null, null),
        }, Array.Empty<TicketEventPin>());

        await enricher.EnrichAsync(detail, default);

        Assert.Equal(0, lookup.LoadCalls);
    }

    /// Stub for the batched lookup: every inbound mail id resolves to the one
    /// stub mail row (with the given body-html hash), attachments are served
    /// by owner (mail) or event id, recipients belong to the stub mail.
    private sealed class StubLookup : IMailTimelineLookup
    {
        private readonly MailMessageRow _row;
        private readonly IReadOnlyList<AttachmentRow> _attachments;
        private readonly IReadOnlyList<MailRecipientRow> _recipients;
        public int LoadCalls { get; private set; }
        public IReadOnlyCollection<Guid>? LastMailIds { get; private set; }
        public IReadOnlyCollection<long>? LastEventIds { get; private set; }
        public IReadOnlyCollection<long>? LastSentEventIds { get; private set; }

        public StubLookup(Guid mailId, string bodyHtmlHash, IReadOnlyList<AttachmentRow> attachments, IReadOnlyList<MailRecipientRow>? recipients)
        {
            _row = new MailMessageRow(mailId, "mid", null, "s", "from@x", "", "box@x",
                DateTime.UtcNow, null, bodyHtmlHash, "", null, null, null, null);
            _attachments = attachments;
            _recipients = recipients ?? Array.Empty<MailRecipientRow>();
        }

        public Task<MailTimelineBatch> LoadAsync(
            IReadOnlyCollection<Guid> mailIds, IReadOnlyCollection<long> eventIds,
            IReadOnlyCollection<long> sentMailEventIds, CancellationToken ct)
        {
            LoadCalls++;
            LastMailIds = mailIds; LastEventIds = eventIds; LastSentEventIds = sentMailEventIds;
            var mailsById = mailIds.Distinct().ToDictionary(id => id, _ => _row);
            var bySent = sentMailEventIds.Distinct().ToDictionary(id => id, _ => _row);
            var batch = new MailTimelineBatch(
                mailsById,
                bySent,
                _attachments.Where(a => a.OwnerKind == "Mail").ToLookup(a => a.OwnerId),
                _attachments.Where(a => a.EventId.HasValue).ToLookup(a => a.EventId!.Value),
                _recipients.Select(r => (MailId: _row.Id, Row: r)).ToLookup(x => x.MailId, x => x.Row));
            return Task.FromResult(batch);
        }
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
