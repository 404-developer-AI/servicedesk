using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.IntakeForms;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Outbound;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;
using Servicedesk.Infrastructure.TaggingMailboxes;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Domain.TaggingMailboxes;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Focused coverage for the v0.0.12 step 2 attachment additions to
/// <see cref="OutboundMailService"/>: inline-cid rewrite, total-bytes cap,
/// and ownership-guard on staged attachment ids.
public sealed class OutboundMailServiceTests
{
    private static readonly Guid TicketId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AuthorId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid QueueId  = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string MailboxAddress = "support@desk.test";

    [Fact]
    public async Task Inline_image_url_is_rewritten_to_cid_and_attachment_flagged()
    {
        var att = ReadyImage();
        var (svc, graph, _, repo, _) = Build(attachments: new[] { att });
        var url = $"/api/tickets/{TicketId}/attachments/{att.Id}";
        var bodyHtml = $"<p>See:</p><img src=\"{url}\" alt=\"x\"/>";

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: bodyHtml,
            AttachmentIds: new[] { att.Id }), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        Assert.NotNull(graph.LastMessage);
        Assert.DoesNotContain(url, graph.LastMessage!.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cid:", graph.LastMessage.BodyHtml, StringComparison.OrdinalIgnoreCase);

        var sentAtt = Assert.Single(graph.LastMessage!.Attachments!);
        Assert.True(sentAtt.IsInline);
        Assert.False(string.IsNullOrWhiteSpace(sentAtt.ContentId));

        var assignment = Assert.Single(repo.MailReassignments);
        Assert.True(assignment.Item.IsInline);
        Assert.Equal(sentAtt.ContentId, assignment.Item.ContentId);
        Assert.Equal(TicketId, assignment.TicketId);
    }

    [Fact]
    public async Task Non_inline_attachment_is_passed_through_without_cid()
    {
        var pdf = ReadyAttachment("application/pdf", "report.pdf", 1024);
        var (svc, graph, _, _, _) = Build(attachments: new[] { pdf });

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>See attachment.</p>",
            AttachmentIds: new[] { pdf.Id }), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        var sent = Assert.Single(graph.LastMessage!.Attachments!);
        Assert.False(sent.IsInline);
        Assert.Null(sent.ContentId);
    }

    [Fact]
    public async Task Total_size_above_cap_returns_TooLarge_and_skips_send()
    {
        var big = ReadyAttachment("application/octet-stream", "big.bin", 3 * 1024 * 1024);
        var (svc, graph, _, _, _) = Build(attachments: new[] { big }, maxOutboundBytes: 2 * 1024 * 1024);

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>x</p>",
            AttachmentIds: new[] { big.Id }), default);

        Assert.Equal(OutboundMailStatus.AttachmentTooLarge, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(graph.LastMessage); // Graph never called
    }

    [Fact]
    public async Task Large_attachment_under_cap_is_sent_with_size_and_streamable_content()
    {
        // 8 MB is above Graph's ~3 MB simple-POST limit but under the 25 MB
        // cap — since v0.0.77 this must go through (GraphMailClient picks the
        // upload-session transport from SizeBytes; here we assert the
        // contract it needs: the size and a lazily-openable content stream).
        var big = ReadyAttachment("application/pdf", "big.pdf", 8L * 1024 * 1024);
        var (svc, graph, _, _, _) = Build(attachments: new[] { big }, maxOutboundBytes: 25L * 1024 * 1024);

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>See attachment.</p>",
            AttachmentIds: new[] { big.Id }), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        var sent = Assert.Single(graph.LastMessage!.Attachments!);
        Assert.Equal(8L * 1024 * 1024, sent.SizeBytes);

        await using var content = await sent.OpenContentAsync(default);
        using var reader = new StreamReader(content);
        Assert.Equal("blob-bytes-for-hash-big.pdf", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Unset_cap_setting_falls_back_to_25MB_default()
    {
        // A zero/unset Mail.MaxOutboundTotalBytes falls back to the same
        // 25 MB default the settings registration carries — a 10 MB part
        // passes (the pre-v0.0.77 fallback of 3 MB would have rejected it)
        // while 26 MB still trips the cap.
        var tenMb = ReadyAttachment("application/pdf", "ten.pdf", 10L * 1024 * 1024);
        var (svc, graph, _, _, _) = Build(attachments: new[] { tenMb }, maxOutboundBytes: 0);

        var ok = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>x</p>",
            AttachmentIds: new[] { tenMb.Id }), default);
        Assert.Equal(OutboundMailStatus.Sent, ok.Status);
        Assert.NotNull(graph.LastMessage);

        var huge = ReadyAttachment("application/zip", "huge.zip", 26L * 1024 * 1024);
        var (svc2, graph2, _, _, _) = Build(attachments: new[] { huge }, maxOutboundBytes: 0);

        var rejected = await svc2.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>x</p>",
            AttachmentIds: new[] { huge.Id }), default);
        Assert.Equal(OutboundMailStatus.AttachmentTooLarge, rejected.Status);
        Assert.Null(graph2.LastMessage);
    }

    [Fact]
    public async Task Attachment_owned_by_other_ticket_is_silently_dropped()
    {
        var foreign = ReadyAttachment("application/pdf", "x.pdf", 1024) with
        {
            OwnerId = Guid.NewGuid(), // some other ticket
        };
        var (svc, graph, _, repo, _) = Build(attachments: new[] { foreign });

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>x</p>",
            AttachmentIds: new[] { foreign.Id }), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        Assert.Null(graph.LastMessage!.Attachments);
        Assert.Empty(repo.MailReassignments);
    }

    [Fact]
    public async Task Already_event_linked_attachment_is_silently_dropped()
    {
        var alreadyLinked = ReadyAttachment("application/pdf", "old.pdf", 1024) with { EventId = 99L };
        var (svc, graph, _, repo, _) = Build(attachments: new[] { alreadyLinked });

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>x</p>",
            AttachmentIds: new[] { alreadyLinked.Id }), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        Assert.Null(graph.LastMessage!.Attachments);
        Assert.Empty(repo.MailReassignments);
    }

    [Fact]
    public async Task Mentioned_user_ids_are_filtered_and_persisted_in_metadata()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var (svc, _, _, _, tickets) = Build(knownAgents: new[] { known });

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>cc <span data-type=\"mention\" data-id=\"x\">@x</span></p>",
            MentionedUserIds: new[] { known, unknown }), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        Assert.Equal(1, result.MentionedUserCount);

        Assert.NotNull(tickets.LastEventInput);
        Assert.NotNull(tickets.LastEventInput!.MetadataJson);
        using var doc = JsonDocument.Parse(tickets.LastEventInput.MetadataJson!);
        var arr = doc.RootElement.GetProperty("mentionedUserIds");
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        var ids = arr.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
        Assert.Single(ids);
        Assert.Equal(known, ids[0]);
    }

    [Fact]
    public async Task Empty_mentioned_user_ids_result_in_empty_array_in_metadata()
    {
        // No mentions in the body — the metadata still contains the field
        // as an empty array (keeps the JSON shape stable for the enricher /
        // future notification worker).
        var (svc, _, _, _, tickets) = Build();

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: "<p>No mentions here.</p>"), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        Assert.Equal(0, result.MentionedUserCount);

        using var doc = JsonDocument.Parse(tickets.LastEventInput!.MetadataJson!);
        var arr = doc.RootElement.GetProperty("mentionedUserIds");
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public async Task Template_image_is_copied_to_ticket_and_cid_embedded()
    {
        // v0.0.92 — a mail-template body carries an <img> pointing at the
        // agent-only template-image endpoint. Sending must copy the image
        // onto the ticket, cid-embed it on the wire, and persist a timeline
        // body that references the ticket-owned copy.
        var templateImage = ReadyTemplateImage();
        var (svc, graph, _, repo, tickets) = Build(attachments: new[] { templateImage });
        var templateUrl = $"/api/compose-templates/images/{templateImage.Id}";

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: $"<p>Hi,</p><img src=\"{templateUrl}\" alt=\"banner\">"), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);

        // The copy is staged on this ticket with the same content hash.
        var copy = Assert.Single(repo.UploadedCreates);
        Assert.Equal(TicketId, copy.TicketId);
        Assert.Equal(templateImage.ContentHash, copy.ContentHash);

        // Wire body: template URL gone, replaced by a cid reference; the
        // copy rides along as an inline attachment with a matching cid.
        Assert.DoesNotContain(templateUrl, graph.LastMessage!.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cid:", graph.LastMessage.BodyHtml, StringComparison.OrdinalIgnoreCase);
        var sentAtt = Assert.Single(graph.LastMessage.Attachments!);
        Assert.True(sentAtt.IsInline);
        Assert.False(string.IsNullOrWhiteSpace(sentAtt.ContentId));

        var assignment = Assert.Single(repo.MailReassignments);
        Assert.True(assignment.Item.IsInline);

        // Timeline body: references the ticket-owned copy (renders via the
        // authenticated ticket endpoint), never the deletable template image.
        Assert.DoesNotContain(templateUrl, tickets.LastEventInput!.BodyHtml!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"/api/tickets/{TicketId}/attachments/", tickets.LastEventInput.BodyHtml!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unresolvable_template_image_reference_is_left_untouched()
    {
        var (svc, graph, _, repo, _) = Build();
        var missingUrl = $"/api/compose-templates/images/{Guid.NewGuid()}";

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: $"<img src=\"{missingUrl}\">"), default);

        // The send goes through — a stale template reference degrades to a
        // broken image for the recipient instead of blocking the mail.
        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        Assert.Contains(missingUrl, graph.LastMessage!.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repo.UploadedCreates);
        Assert.Null(graph.LastMessage.Attachments);
    }

    [Fact]
    public async Task Non_template_attachment_id_in_template_url_is_not_copied()
    {
        // Exfiltration guard: wrapping another ticket's attachment id in a
        // template-image URL must not copy that attachment onto this ticket.
        var foreign = ReadyAttachment("image/png", "secret.png", 1024) with
        {
            OwnerId = Guid.NewGuid(), // some other ticket
        };
        var (svc, graph, _, repo, _) = Build(attachments: new[] { foreign });
        var smuggledUrl = $"/api/compose-templates/images/{foreign.Id}";

        var result = await svc.SendAsync(new OutboundMailRequest(
            TicketId, AuthorId, OutboundMailKind.New,
            To: new[] { new GraphRecipient("dest@example.com", "D") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            Subject: "Subj",
            BodyHtml: $"<img src=\"{smuggledUrl}\">"), default);

        Assert.Equal(OutboundMailStatus.Sent, result.Status);
        Assert.Empty(repo.UploadedCreates);
        Assert.Null(graph.LastMessage!.Attachments);
        Assert.Contains(smuggledUrl, graph.LastMessage.BodyHtml, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ----

    private static AttachmentRow ReadyTemplateImage()
    {
        // Self-owned row, exactly as CreateForComposeTemplateImageAsync
        // writes it: owner_kind='ComposeTemplateImage', owner_id = own id.
        var id = Guid.NewGuid();
        return new AttachmentRow(
            Id: id, OwnerId: id, OwnerKind: "ComposeTemplateImage",
            ContentHash: "hash-template-img", SizeBytes: 2048, MimeType: "image/png",
            OriginalFilename: "banner.png", IsInline: false, ContentId: null,
            ProcessingState: "Ready", EventId: null);
    }

    private static AttachmentRow ReadyImage() =>
        new(Id: Guid.NewGuid(), OwnerId: TicketId, OwnerKind: "Ticket",
            ContentHash: "hash-img", SizeBytes: 4096, MimeType: "image/png",
            OriginalFilename: "logo.png", IsInline: false, ContentId: null,
            ProcessingState: "Ready", EventId: null);

    private static AttachmentRow ReadyAttachment(string mime, string name, long size) =>
        new(Id: Guid.NewGuid(), OwnerId: TicketId, OwnerKind: "Ticket",
            ContentHash: "hash-" + name, SizeBytes: size, MimeType: mime,
            OriginalFilename: name, IsInline: false, ContentId: null,
            ProcessingState: "Ready", EventId: null);

    private static (
        OutboundMailService svc,
        StubGraph graph,
        StubMail mail,
        StubAttachments attachments,
        StubTickets tickets) Build(
        IReadOnlyList<AttachmentRow>? attachments = null,
        long? maxOutboundBytes = null,
        IEnumerable<Guid>? knownAgents = null)
    {
        var graph = new StubGraph();
        var taxonomy = new StubTaxonomy();
        var tickets = new StubTickets();
        var mail = new StubMail();
        var atts = new StubAttachments(attachments ?? Array.Empty<AttachmentRow>());
        var blobs = new StubBlobs();
        var settings = new StubSettings(maxOutboundBytes ?? (3L * 1024 * 1024));
        var sla = new StubSla();
        var users = new StubUsers(knownAgents);
        var mentions = new StubMentions();
        var taggingMailboxes = new StubTaggingMailboxes();
        var intakeForms = new StubIntakeForms();
        var intakeTokens = new StubIntakeTokens();
        var svc = new OutboundMailService(graph, taxonomy, tickets, mail, atts, blobs, settings, sla, users, mentions,
            taggingMailboxes, intakeForms, intakeTokens, new NoopTriggerService(), new NoopSignatureComposer(),
            NullLogger<OutboundMailService>.Instance);
        return (svc, graph, mail, atts, tickets);
    }

    private sealed class StubGraph : IGraphMailClient
    {
        public GraphOutboundMessage? LastMessage { get; private set; }
        public Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage message, CancellationToken ct)
        {
            LastMessage = message;
            return Task.FromResult(new GraphSentMailResult("msg-id@graph", DateTimeOffset.UtcNow));
        }
        public Task<GraphFullMessage> FetchMessageAsync(string mbx, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Stream> FetchRawMessageAsync(string mbx, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<GraphDeltaPage> ListInboxDeltaAsync(string m, string f, string? d, int b, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string m, CancellationToken ct) => throw new NotImplementedException();
        public Task<TimeSpan> PingAsync(string mbx, CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
        public Task MarkAsReadAsync(string m, string id, CancellationToken ct) => Task.CompletedTask;
        public Task MoveAsync(string m, string id, string f, CancellationToken ct) => Task.CompletedTask;
        public Task<string> EnsureFolderAsync(string m, string n, CancellationToken ct) => Task.FromResult("f");
        public Task<Stream> FetchAttachmentBytesAsync(string m, string id, string aid, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubTaxonomy : ITaxonomyRepository
    {
        private static readonly Queue Q = new(
            QueueId, "Support", "support", "", "#fff", "", 0, true, false,
            DateTime.UtcNow, DateTime.UtcNow, MailboxAddress, MailboxAddress);

        public Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct) => Task.FromResult<Queue?>(Q);
        public Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string description, string color, string icon, int sortOrder, bool isActive, string? inbound, string? outbound, string? inboundFolderId, string? inboundFolderName, IReadOnlyList<Guid> allowedStatusIds, Guid? defaultStatusId, bool aiAssistEnabled, string timeAlertMode, int? timeAlertThresholdMinutes, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetQueueInboundPollingAsync(Guid id, bool enabled, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketType>> ListTicketTypesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType?> GetTicketTypeAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType?> GetTicketTypeByCodeAsync(string code, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType> CreateTicketTypeAsync(TicketType t, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType?> UpdateTicketTypeAsync(Guid id, string code, string label, string description, string icon, string color, int sortOrder, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteTicketTypeAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Status>> ListStatusesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Status?> GetStatusAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Status> CreateStatusAsync(Status s, CancellationToken ct) => throw new NotImplementedException();
        public Task<Status?> UpdateStatusAsync(Guid id, string name, string slug, string stateCategory, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteStatusAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Priority>> ListPrioritiesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> GetPriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority> CreatePriorityAsync(Priority p, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> UpdatePriorityAsync(Guid id, string name, string slug, int level, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeletePriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Category?> GetCategoryAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Category> CreateCategoryAsync(Category c, CancellationToken ct) => throw new NotImplementedException();
        public Task<Category?> UpdateCategoryAsync(Guid id, Guid? parentId, string name, string slug, string description, int sortOrder, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteCategoryAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubTickets : ITicketRepository
    {
        private readonly TicketDetail _detail = MakeDetail();
        private long _nextEventId = 1000;

        /// Last event passed to AddEventAsync — tests read its MetadataJson
        /// to assert the mention-filter persisted the right ids.
        public NewTicketEvent? LastEventInput { get; private set; }

        public Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<TicketDetail?>(_detail);

        public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
        {
            LastEventInput = input;
            return Task.FromResult<TicketEvent?>(new TicketEvent(
                Id: global::System.Threading.Interlocked.Increment(ref _nextEventId),
                TicketId: ticketId, EventType: input.EventType,
                AuthorUserId: input.AuthorUserId, AuthorContactId: input.AuthorContactId,
                AuthorName: null, BodyText: input.BodyText, BodyHtml: input.BodyHtml,
                MetadataJson: input.MetadataJson ?? "{}", IsInternal: input.IsInternal,
                CreatedUtc: DateTime.UtcNow, EditedUtc: null, EditedByUserId: null));
        }

        public Task<TicketPage> SearchAsync(TicketQuery q, VisibilityScope s, Guid? u, Guid? c, CancellationToken ct) => throw new NotImplementedException();
        public Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> UpdateFieldsAsync(Guid t, TicketFieldUpdate u, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> AssignCompanyAsync(Guid t, Guid c, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> ChangeRequesterAsync(Guid t, Guid c, Guid? co, bool aw, string? rv, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEvent?> UpdateEventAsync(Guid t, long e, UpdateTicketEvent u, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid t, long e, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> PinEventAsync(Guid t, long e, Guid u, string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UnpinEventAsync(Guid t, long e, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> UpdatePinRemarkAsync(Guid t, long e, string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> EventBelongsToTicketAsync(Guid t, long e, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> InsertFakeBatchAsync(int c, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> IsTitleReviewedAsync(Guid ticketId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkTitleReviewedAsync(Guid ticketId, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(string? s, Guid e, IReadOnlyCollection<Guid>? q, Guid? r, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TicketPickerHit>>(Array.Empty<TicketPickerHit>());
        public Task<IReadOnlyList<long>> GetMergedSourceTicketNumbersAsync(Guid t, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        public Task<MergeResult?> MergeAsync(Guid s, Guid t, Guid a, bool ack, CancellationToken ct)
            => Task.FromResult<MergeResult?>(null);
        public Task<IReadOnlyList<SplitChildTicket>> GetSplitChildrenAsync(Guid p, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SplitChildTicket>>(Array.Empty<SplitChildTicket>());
        public Task<SplitResult?> SplitAsync(Guid s, long e, string subj, Guid a, string? oh, string? ot, CancellationToken ct)
            => Task.FromResult<SplitResult?>(null);
        public Task<IReadOnlyList<LinkedChildTicket>> GetChildTicketsAsync(Guid p, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LinkedChildTicket>>(Array.Empty<LinkedChildTicket>());
        public Task<ParentTicketSummary?> GetParentSummaryAsync(Guid t, CancellationToken ct)
            => Task.FromResult<ParentTicketSummary?>(null);
        public Task<LinkParentResult> LinkParentAsync(Guid t, Guid p, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UnlinkParentAsync(Guid t, Guid a, CancellationToken ct) => throw new NotImplementedException();

        private static TicketDetail MakeDetail()
        {
            var now = DateTime.UtcNow;
            var t = new Ticket(TicketId, 4242, "Subj", Guid.NewGuid(), null, QueueId,
                Guid.NewGuid(), Guid.NewGuid(), null, "Api", null, now, now, null, null, null, null, false);
            return new TicketDetail(t,
                new TicketBody(TicketId, "", null),
                Array.Empty<TicketEvent>(),
                Array.Empty<TicketEventPin>());
        }
    }

    private sealed class StubMail : IMailMessageRepository
    {
        public List<NewOutboundMailMessage> Outbound { get; } = new();
        public Task<MailMessageRow?> GetByMessageIdAsync(string m, CancellationToken ct) => Task.FromResult<MailMessageRow?>(null);
        public Task<MailMessageRow?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<MailMessageRow?>(null);
        public Task<MailMessageRow?> GetByTicketEventIdAsync(long ticketEventId, CancellationToken ct) => Task.FromResult<MailMessageRow?>(null);
        public Task<Guid?> FindTicketIdByReferencesAsync(IReadOnlyList<string> ids, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<Guid> InsertAsync(NewMailMessage row, IReadOnlyList<NewMailRecipient> r, IReadOnlyList<NewMailAttachment> a, CancellationToken ct) => throw new NotImplementedException();
        public Task AttachToTicketAsync(Guid m, Guid t, long e, CancellationToken ct) => Task.CompletedTask;
        public Task MarkMailboxMovedAsync(Guid m, DateTime u, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FinalizeCandidate>> ListReadyForFinalizeAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<FinalizeCandidate>>(Array.Empty<FinalizeCandidate>());
        public Task<FinalizeCandidate?> GetIfReadyForFinalizeAsync(Guid mailId, CancellationToken ct) => Task.FromResult<FinalizeCandidate?>(null);
        public Task<Guid> InsertOutboundAsync(NewOutboundMailMessage row, IReadOnlyList<NewMailRecipient> r, CancellationToken ct)
        {
            Outbound.Add(row);
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<MailThreadAnchor?> GetLatestThreadAnchorAsync(Guid t, CancellationToken ct) => Task.FromResult<MailThreadAnchor?>(null);
        public Task<IReadOnlyList<MailRecipientRow>> ListRecipientsAsync(Guid m, CancellationToken ct) => Task.FromResult<IReadOnlyList<MailRecipientRow>>(Array.Empty<MailRecipientRow>());
        public Task<MailMessageRow?> GetFirstInboundForTicketAsync(Guid t, CancellationToken ct) => Task.FromResult<MailMessageRow?>(null);
    }

    private sealed class StubAttachments : IAttachmentRepository
    {
        private readonly Dictionary<Guid, AttachmentRow> _rows;
        public List<(AttachmentReassignToMail Item, Guid TicketId, Guid MailMessageId, long EventId)> MailReassignments { get; } = new();

        /// Every NewUploadedAttachment passed to CreateUploadedAsync — the
        /// template-image tests read this to assert the ticket-side copy.
        public List<NewUploadedAttachment> UploadedCreates { get; } = new();

        public StubAttachments(IReadOnlyList<AttachmentRow> rows)
        {
            _rows = rows.ToDictionary(r => r.Id);
        }

        public Task<AttachmentRow?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_rows.TryGetValue(id, out var r) ? r : null);

        public Task<IReadOnlyList<AttachmentRow>> ListByMailAsync(Guid mailId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AttachmentRow>>(Array.Empty<AttachmentRow>());
        public Task<IReadOnlyList<AttachmentRow>> ListByEventAsync(long eventId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AttachmentRow>>(Array.Empty<AttachmentRow>());
        public Task<bool> MarkReadyAsync(Guid id, string h, long s, string m, CancellationToken ct) => Task.FromResult(true);
        public Task MarkFailedAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<Guid> CreateUploadedAsync(NewUploadedAttachment input, CancellationToken ct)
        {
            // Behaves like the real repo: the copy lands as a Ready row staged
            // on the ticket, resolvable through GetByIdAsync in the same send.
            UploadedCreates.Add(input);
            var id = Guid.NewGuid();
            _rows[id] = new AttachmentRow(
                Id: id, OwnerId: input.TicketId, OwnerKind: "Ticket",
                ContentHash: input.ContentHash, SizeBytes: input.SizeBytes,
                MimeType: input.MimeType, OriginalFilename: input.OriginalFilename,
                IsInline: false, ContentId: null, ProcessingState: "Ready", EventId: null);
            return Task.FromResult(id);
        }
        public Task<int> ReassignToEventAsync(IReadOnlyList<Guid> ids, Guid ticketId, long eventId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ReassignToMailAsync(IReadOnlyList<AttachmentReassignToMail> assignments, Guid ticketId, Guid mailMessageId, long ticketEventId, CancellationToken ct)
        {
            foreach (var a in assignments) MailReassignments.Add((a, ticketId, mailMessageId, ticketEventId));
            return Task.FromResult(assignments.Count);
        }
        public Task<Guid> CreateForKbArticleAsync(NewKbArticleAttachment input, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AttachmentRow>> ListByKbArticleAsync(Guid articleId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteKbAttachmentAsync(Guid attachmentId, Guid articleId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> CreateForFeedbackEntryAsync(NewFeedbackEntryAttachment input, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AttachmentRow>> ListByFeedbackEntryAsync(Guid entryId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteFeedbackAttachmentAsync(Guid attachmentId, Guid entryId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> CreateForComposeTemplateImageAsync(NewComposeTemplateImage input, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubBlobs : IBlobStore
    {
        public Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken ct = default)
            => Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("blob-bytes-for-" + contentHash)));
        public Task<bool> ExistsAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class StubSettings : ISettingsService
    {
        private readonly long _maxOutboundBytes;
        public StubSettings(long maxOutboundBytes) { _maxOutboundBytes = maxOutboundBytes; }
        public Task<T> GetAsync<T>(string key, CancellationToken ct = default)
        {
            object value = key switch
            {
                SettingKeys.Mail.PlusAddressToken => "TCK",
                SettingKeys.Mail.MaxOutboundTotalBytes => _maxOutboundBytes,
                _ => default(T)!,
            };
            return Task.FromResult((T)value!);
        }
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubSla : ISlaEngine
    {
        public Task OnTicketCreatedAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
        public Task OnTicketEventAsync(Guid ticketId, string eventType, CancellationToken ct) => Task.CompletedTask;
        public Task OnTicketFieldsChangedAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
        public Task RecalcAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubMentions : IMentionNotificationService
    {
        public List<MentionNotificationSource> Published { get; } = new();
        public Task PublishAsync(MentionNotificationSource source, CancellationToken ct)
        {
            Published.Add(source);
            return Task.CompletedTask;
        }
    }

    private sealed class StubTaggingMailboxes : ITaggingMailboxRepository
    {
        public Task<IReadOnlyList<TaggingMailbox>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TaggingMailbox>>(Array.Empty<TaggingMailbox>());
        public Task<TaggingMailbox?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<TaggingMailbox?>(null);
        public Task<IReadOnlyList<TaggingMailbox>> SearchActiveAsync(string? search, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TaggingMailbox>>(Array.Empty<TaggingMailbox>());
        public Task<IReadOnlyList<TaggingMailbox>> ResolveActiveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TaggingMailbox>>(Array.Empty<TaggingMailbox>());
        public Task<Guid> CreateAsync(string name, string email, bool isActive, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid());
        public Task<bool> UpdateAsync(Guid id, string name, string email, bool isActive, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class StubUsers : IUserService
    {
        private readonly HashSet<Guid> _knownAgents;

        public StubUsers(IEnumerable<Guid>? knownAgents = null)
        {
            _knownAgents = new HashSet<Guid>(knownAgents ?? Array.Empty<Guid>());
        }

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<ApplicationUser?> CreateFirstAdminAsync(string email, string passwordHash, CancellationToken ct = default) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByExternalAsync(string provider, string subject, CancellationToken ct = default) => Task.FromResult<ApplicationUser?>(null);
        public Task MarkInactiveAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentUser>> ListAgentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentUser>>(Array.Empty<AgentUser>());
        public Task<IReadOnlyList<AgentUser>> SearchAgentsAsync(string? search, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentUser>>(Array.Empty<AgentUser>());
        public Task<IReadOnlyList<Guid>> FilterAgentIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            var filtered = ids.Distinct().Where(id => _knownAgents.Contains(id)).ToList();
            return Task.FromResult<IReadOnlyList<Guid>>(filtered);
        }
        public Task UpdatePasswordHashAsync(Guid userId, string newHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordSuccessfulLoginAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RecordFailedLoginAsync(Guid userId, int maxAttempts, int windowSeconds, int lockoutDurationSeconds, CancellationToken ct = default) => Task.FromResult(false);
        public Task<TimesheetFlags> GetTimesheetFlagsAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(TimesheetFlags.None);
        public Task<IsoFlags> GetIsoFlagsAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(IsoFlags.None);
        public Task<bool> GetKbEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetSearchEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> GetActivityFeedEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetAssetsEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetAdsolutTimesheetEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetAdsolutOrdersEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetTimesheetBackofficeEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetStatisticsReadEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetStatisticsWriteEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetContractsEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> GetFeedbackEnabledAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<FeedbackAccessInfo> GetFeedbackAccessAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(default(FeedbackAccessInfo));
        public Task<IReadOnlySet<string>> GetSearchFeatureFlagsAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class StubIntakeForms : IIntakeFormRepository
    {
        public Task<Guid> CreateDraftAsync(Guid ticketId, Guid templateId, string prefillJson, Guid? createdBy, CancellationToken ct) => Task.FromResult(Guid.Empty);
        public Task<bool> UpdateDraftPrefillAsync(Guid instanceId, Guid ticketId, string prefillJson, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> DeleteDraftAsync(Guid instanceId, Guid ticketId, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<IntakeFormInstanceSummary>> ListForTicketAsync(Guid ticketId, CancellationToken ct) => Task.FromResult<IReadOnlyList<IntakeFormInstanceSummary>>(Array.Empty<IntakeFormInstanceSummary>());
        public Task<IntakeFormAgentView?> GetAgentViewAsync(Guid ticketId, Guid instanceId, CancellationToken ct) => Task.FromResult<IntakeFormAgentView?>(null);
        public Task<long?> SendDraftAsync(Guid instanceId, Guid ticketId, Guid actorUserId, byte[] tokenHash, byte[] tokenCipher, DateTime expiresUtc, string sentToEmail, string metadataJson, CancellationToken ct) => Task.FromResult<long?>(null);
        public Task<bool> CancelSentAsync(Guid instanceId, Guid ticketId, Guid actorUserId, CancellationToken ct) => Task.FromResult(false);
        public Task<IntakePublicView?> GetByTokenHashForPublicAsync(byte[] tokenHash, CancellationToken ct) => Task.FromResult<IntakePublicView?>(null);
        public Task<SubmitResult?> TrySubmitAsync(byte[] tokenHash, IReadOnlyList<IntakeFormSubmitAnswer> answers, string? ip, string? userAgent, DateTime nowUtc, bool autoPin, CancellationToken ct) => Task.FromResult<SubmitResult?>(null);
        public Task<IReadOnlyList<ExpiredInstance>> ExpireStaleAsync(int maxBatch, DateTime nowUtc, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExpiredInstance>>(Array.Empty<ExpiredInstance>());
    }

    private sealed class StubIntakeTokens : IIntakeFormTokenService
    {
        public (string Raw, byte[] Hash, byte[] Cipher) Mint() => ("stub", Array.Empty<byte>(), Array.Empty<byte>());
        public byte[]? HashForLookup(string rawFromUrl) => null;
    }

    private sealed class NoopTriggerService : ITriggerService
    {
        public Task EvaluateAsync(
            Guid ticketId, long? ticketEventId, TriggerActivatorKind activatorKind,
            TriggerChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

        public Task<TriggerScheduledRunResult> EvaluateScheduledAsync(
            Guid triggerId, Guid ticketId, DateTime boundaryUtc,
            string expectedActivatorMode, CancellationToken ct)
            => Task.FromResult(new TriggerScheduledRunResult(null, ChainShouldClear: false));

        public Task<TriggerDryRunResult?> DryRunAsync(
            Guid triggerId, Guid ticketId, CancellationToken ct)
            => Task.FromResult<TriggerDryRunResult?>(null);
    }

    // No signature is ever appended in these tests, so both compose paths
    // return null — the outbound body assertions stay exactly as before.
    private sealed class NoopSignatureComposer : Servicedesk.Infrastructure.Signatures.ISignatureComposer
    {
        public Task<Servicedesk.Infrastructure.Signatures.ComposedSignature?> ComposeForQueueAsync(
            Guid queueId, Guid senderUserId, bool isReply, CancellationToken ct)
            => Task.FromResult<Servicedesk.Infrastructure.Signatures.ComposedSignature?>(null);

        public Task<Servicedesk.Infrastructure.Signatures.ComposedSignature?> ComposeSystemAsync(
            bool isReply, CancellationToken ct)
            => Task.FromResult<Servicedesk.Infrastructure.Signatures.ComposedSignature?>(null);

        public Task<string?> ComposePreviewForQueueAsync(
            Guid queueId, Guid senderUserId, bool isReply, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }
}
