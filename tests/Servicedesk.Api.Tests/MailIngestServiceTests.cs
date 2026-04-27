using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Companies;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class MailIngestServiceTests
{
    private static readonly Guid QueueId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string QueueMailbox = "inbox@test";

    [Fact]
    public async Task Skips_auto_submitted_header_other_than_no()
    {
        var (svc, graph, _, _, _) = Build();
        graph.Message = NewMessage(autoSubmitted: "auto-replied");

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.SkippedAutoSubmitted, result.Outcome);
    }

    [Fact]
    public async Task Skips_when_from_matches_own_mailbox()
    {
        var (svc, graph, _, _, _) = Build();
        graph.Message = NewMessage(from: new GraphRecipient(QueueMailbox, "self"));

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.SkippedOwnMailbox, result.Outcome);
    }

    [Fact]
    public async Task Deduplicates_known_message_id()
    {
        var (svc, graph, mailRepo, _, _) = Build();
        graph.Message = NewMessage(messageId: "abc@example");
        mailRepo.ByMessageId["abc@example"] = new MailMessageRow(
            Guid.NewGuid(), "abc@example", null, "s", "x@y", "", QueueMailbox,
            DateTime.UtcNow, null, null, "", Guid.NewGuid(), 42L, null, null);

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Deduplicated, result.Outcome);
    }

    [Fact]
    public async Task Creates_new_ticket_when_no_thread_match()
    {
        var (svc, graph, mailRepo, tickets, _) = Build();
        graph.Message = NewMessage(messageId: "new@example");

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Created, result.Outcome);
        Assert.NotNull(result.TicketId);
        Assert.Single(tickets.Created);
        Assert.Equal("Mail", tickets.Created[0].Source);
        Assert.Single(mailRepo.Inserts);
    }

    [Fact]
    public async Task Appends_when_plus_address_matches_existing_ticket()
    {
        var (svc, graph, _, tickets, _) = Build();
        var existing = Guid.NewGuid();
        tickets.NumberToId[1234] = existing;
        graph.Message = NewMessage(
            messageId: "reply@example",
            to: new[] { new GraphRecipient("servicedesk+TCK-1234@domain.com", "Desk") });

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Appended, result.Outcome);
        Assert.Equal(existing, result.TicketId);
        Assert.Empty(tickets.Created);
    }

    [Fact]
    public async Task Attachments_on_graph_message_are_forwarded_to_repository()
    {
        var (svc, graph, mailRepo, _, _) = Build();
        graph.Message = NewMessage(messageId: "withatt@example") with
        {
            Attachments = new[]
            {
                new GraphAttachmentInfo("gid-att-1", "photo.jpg", "image/jpeg", 12345, true, "img-001"),
                new GraphAttachmentInfo("gid-att-2", "report.pdf", "application/pdf", 60000, false, null),
            },
        };

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-msg", default);

        Assert.Equal(MailIngestOutcome.Created, result.Outcome);
        Assert.Equal(2, mailRepo.InsertedAttachments.Count);
        var inline = mailRepo.InsertedAttachments.Single(a => a.IsInline);
        Assert.Equal("img-001", inline.ContentId);
        Assert.Equal("gid-att-1", inline.GraphAttachmentId);
        Assert.Equal("graph-id", inline.GraphMessageId);
        Assert.Equal(QueueMailbox, inline.Mailbox);
    }

    [Fact]
    public async Task Appends_via_in_reply_to_reference()
    {
        var (svc, graph, mailRepo, tickets, _) = Build();
        var existing = Guid.NewGuid();
        mailRepo.ReferenceLookup["parent@example"] = existing;
        graph.Message = NewMessage(messageId: "child@example", inReplyTo: "<parent@example>");

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Appended, result.Outcome);
        Assert.Equal(existing, result.TicketId);
        Assert.Empty(tickets.Created);
    }

    // v0.0.9 step 3: thread-reply must not re-run the decision tree. The
    // existing ticket already has its own company_id / resolved_via frozen at
    // creation; touching it on every reply would silently migrate tickets
    // between companies and break audit integrity.
    [Fact]
    public async Task Thread_reply_does_not_invoke_company_resolution()
    {
        var (svc, graph, _, tickets, contacts) = Build();
        contacts.NextResolution = new CompanyResolution(Guid.NewGuid(), "primary", false);
        var existing = Guid.NewGuid();
        tickets.NumberToId[9999] = existing;
        graph.Message = NewMessage(
            messageId: "reply@example",
            to: new[] { new GraphRecipient("servicedesk+TCK-9999@domain.com", "Desk") });

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Appended, result.Outcome);
        Assert.Equal(0, contacts.ResolveCalls);
        Assert.Empty(tickets.Created);
    }

    [Fact]
    public async Task New_ticket_writes_resolution_fields_from_decision_tree()
    {
        var (svc, graph, _, tickets, contacts) = Build();
        var companyId = Guid.NewGuid();
        contacts.NextResolution = new CompanyResolution(companyId, "secondary", Awaiting: false);
        graph.Message = NewMessage(messageId: "fresh-resolve@example");

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Created, result.Outcome);
        Assert.Equal(1, contacts.ResolveCalls);
        var created = Assert.Single(tickets.Created);
        Assert.Equal(companyId, created.CompanyId);
        Assert.Equal("secondary", created.CompanyResolvedVia);
        Assert.False(created.AwaitingCompanyAssignment);
    }

    // v0.0.23: a reply on a thread whose ticket has been merged must land on
    // the surviving target. The resolver follows merged_into_ticket_id up to
    // 10 hops so multi-step chains (A → B → C) still route correctly.
    [Fact]
    public async Task Plus_address_follows_merge_chain_to_final_target()
    {
        var (svc, graph, _, tickets, _) = Build();
        var middle = Guid.NewGuid();
        var final = Guid.NewGuid();
        tickets.NumberToId[1234] = middle;
        tickets.MergedInto[middle] = final;
        graph.Message = NewMessage(
            messageId: "reply@example",
            to: new[] { new GraphRecipient("servicedesk+TCK-1234@domain.com", "Desk") });

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Appended, result.Outcome);
        Assert.Equal(final, result.TicketId);
    }

    [Fact]
    public async Task In_reply_to_follows_multi_hop_merge_chain()
    {
        var (svc, graph, mailRepo, tickets, _) = Build();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        mailRepo.ReferenceLookup["parent@example"] = first;
        tickets.MergedInto[first] = second;
        tickets.MergedInto[second] = third;
        graph.Message = NewMessage(messageId: "child@example", inReplyTo: "<parent@example>");

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Appended, result.Outcome);
        Assert.Equal(third, result.TicketId);
    }

    [Fact]
    public async Task New_ticket_marks_awaiting_when_resolution_unresolved()
    {
        var (svc, graph, _, tickets, contacts) = Build();
        contacts.NextResolution = new CompanyResolution(null, "unresolved", Awaiting: true);
        graph.Message = NewMessage(messageId: "fresh-ambiguous@example");

        var result = await svc.IngestAsync(QueueId, QueueMailbox, "gid-1", default);

        Assert.Equal(MailIngestOutcome.Created, result.Outcome);
        var created = Assert.Single(tickets.Created);
        Assert.Null(created.CompanyId);
        Assert.Equal("unresolved", created.CompanyResolvedVia);
        Assert.True(created.AwaitingCompanyAssignment);
    }

    private static GraphFullMessage NewMessage(
        string messageId = "fresh@example",
        GraphRecipient? from = null,
        IReadOnlyList<GraphRecipient>? to = null,
        string? inReplyTo = null,
        string? autoSubmitted = null,
        string subject = "Hello there")
        => new(
            Id: "graph-id",
            InternetMessageId: messageId,
            InReplyTo: inReplyTo,
            References: null,
            Subject: subject,
            From: from ?? new GraphRecipient("sender@external.com", "Sender"),
            To: to ?? new[] { new GraphRecipient("desk@test", "Desk") },
            Cc: Array.Empty<GraphRecipient>(),
            Bcc: Array.Empty<GraphRecipient>(),
            BodyHtml: "<p>Body</p>",
            BodyText: "Body",
            ReceivedUtc: DateTimeOffset.UtcNow,
            AutoSubmitted: autoSubmitted,
            Attachments: Array.Empty<GraphAttachmentInfo>());

    private static (MailIngestService svc, StubGraph graph, StubMailRepo mail, StubTickets tickets, StubContacts contacts) Build()
    {
        var graph = new StubGraph();
        var mail = new StubMailRepo();
        var tickets = new StubTickets();
        var taxonomy = new StubTaxonomy();
        var contacts = new StubContacts();
        var blobs = new StubBlobs();
        var settings = new StubSettings();
        var svc = new MailIngestService(graph, mail, tickets, taxonomy, contacts, blobs, settings,
            new NoopSlaEngine(), NullLogger<MailIngestService>.Instance);
        return (svc, graph, mail, tickets, contacts);
    }

    private sealed class StubGraph : IGraphMailClient
    {
        public GraphFullMessage Message { get; set; } = null!;
        public Task<GraphFullMessage> FetchMessageAsync(string mbx, string id, CancellationToken ct)
            => Task.FromResult(Message);
        public Task<Stream> FetchRawMessageAsync(string mbx, string id, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("raw")));
        public Task<GraphDeltaPage> ListInboxDeltaAsync(string m, string f, string? d, int b, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string m, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GraphMailFolderInfo>>(Array.Empty<GraphMailFolderInfo>());
        public Task<TimeSpan> PingAsync(string mbx, CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
        public Task MarkAsReadAsync(string m, string id, CancellationToken ct) => Task.CompletedTask;
        public Task MoveAsync(string m, string id, string f, CancellationToken ct) => Task.CompletedTask;
        public Task<string> EnsureFolderAsync(string m, string n, CancellationToken ct) => Task.FromResult("f");
        public Task<Stream> FetchAttachmentBytesAsync(string m, string id, string aid, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>()));
        public Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage m, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubMailRepo : IMailMessageRepository
    {
        public Dictionary<string, MailMessageRow> ByMessageId { get; } = new();
        public Dictionary<string, Guid> ReferenceLookup { get; } = new();
        public List<NewMailMessage> Inserts { get; } = new();

        public Task<MailMessageRow?> GetByMessageIdAsync(string messageId, CancellationToken ct)
            => Task.FromResult(ByMessageId.TryGetValue(messageId, out var r) ? r : null);
        public Task<MailMessageRow?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<MailMessageRow?>(null);
        public Task<Guid?> FindTicketIdByReferencesAsync(IReadOnlyList<string> messageIds, CancellationToken ct)
        {
            foreach (var m in messageIds)
            {
                var trimmed = m.Trim('<', '>');
                if (ReferenceLookup.TryGetValue(trimmed, out var g))
                    return Task.FromResult<Guid?>(g);
            }
            return Task.FromResult<Guid?>(null);
        }
        public List<NewMailAttachment> InsertedAttachments { get; } = new();
        public Task<Guid> InsertAsync(NewMailMessage row, IReadOnlyList<NewMailRecipient> recipients,
            IReadOnlyList<NewMailAttachment> attachments, CancellationToken ct)
        {
            Inserts.Add(row);
            InsertedAttachments.AddRange(attachments);
            return Task.FromResult(Guid.NewGuid());
        }
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

    private sealed class StubTickets : ITicketRepository, ITicketNumberLookup
    {
        public List<NewTicket> Created { get; } = new();
        public Dictionary<long, Guid> NumberToId { get; } = new();
        public Dictionary<Guid, Guid> MergedInto { get; } = new();

        public Task<Guid?> GetIdByNumberAsync(long number, CancellationToken ct)
            => Task.FromResult(NumberToId.TryGetValue(number, out var g) ? (Guid?)g : null);

        public Task<Guid?> GetMergedIntoAsync(Guid ticketId, CancellationToken ct)
            => Task.FromResult(MergedInto.TryGetValue(ticketId, out var g) ? (Guid?)g : null);

        public Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct)
        {
            Created.Add(input);
            var now = DateTime.UtcNow;
            return Task.FromResult(new Ticket(
                Guid.NewGuid(), 1000 + Created.Count, input.Subject,
                input.RequesterContactId, input.AssigneeUserId, input.QueueId,
                input.StatusId, input.PriorityId, input.CategoryId,
                input.Source, null, now, now, null, null, null, null, false));
        }

        public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
            => Task.FromResult<TicketEvent?>(new TicketEvent(
                Id: Random.Shared.NextInt64(1, 1_000_000),
                TicketId: ticketId,
                EventType: input.EventType,
                AuthorUserId: input.AuthorUserId,
                AuthorContactId: input.AuthorContactId,
                AuthorName: null,
                BodyText: input.BodyText,
                BodyHtml: input.BodyHtml,
                MetadataJson: input.MetadataJson ?? "{}",
                IsInternal: input.IsInternal,
                CreatedUtc: DateTime.UtcNow,
                EditedUtc: null,
                EditedByUserId: null));

        public Task<TicketPage> SearchAsync(TicketQuery q, VisibilityScope s, Guid? uid, Guid? cid, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> UpdateFieldsAsync(Guid t, TicketFieldUpdate u, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> AssignCompanyAsync(Guid t, Guid c, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> ChangeRequesterAsync(Guid t, Guid c, Guid? co, bool aw, string? rv, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEvent?> UpdateEventAsync(Guid t, long e, UpdateTicketEvent u, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid t, long e, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> PinEventAsync(Guid t, long e, Guid u, string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UnpinEventAsync(Guid t, long e, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> UpdatePinRemarkAsync(Guid t, long e, string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> EventBelongsToTicketAsync(Guid t, long e, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> InsertFakeBatchAsync(int c, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(string? s, Guid e, IReadOnlyCollection<Guid>? q, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TicketPickerHit>>(Array.Empty<TicketPickerHit>());
        public Task<IReadOnlyList<long>> GetMergedSourceTicketNumbersAsync(Guid t, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        public Task<MergeResult?> MergeAsync(Guid s, Guid t, Guid a, bool ack, CancellationToken ct)
            => Task.FromResult<MergeResult?>(null);
        public Task<IReadOnlyList<SplitChildTicket>> GetSplitChildrenAsync(Guid p, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SplitChildTicket>>(Array.Empty<SplitChildTicket>());
        public Task<SplitResult?> SplitAsync(Guid s, long e, string subj, Guid a, string? oh, string? ot, CancellationToken ct)
            => Task.FromResult<SplitResult?>(null);
    }

    private sealed class StubTaxonomy : ITaxonomyRepository
    {
        private static readonly Queue Q = new(
            QueueId, "desk", "desk", "", "#fff", "", 0, true, false,
            DateTime.UtcNow, DateTime.UtcNow, QueueMailbox, null);
        private static readonly Status S = new(
            Guid.NewGuid(), "New", "new", "New", "#fff", "", 0, true, false, true, DateTime.UtcNow, DateTime.UtcNow);
        private static readonly Priority P = new(
            Guid.NewGuid(), "Normal", "normal", 3, "#fff", "", 0, true, false, true, DateTime.UtcNow, DateTime.UtcNow);

        public Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Queue>>(new[] { Q });
        public Task<IReadOnlyList<Status>> ListStatusesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Status>>(new[] { S });
        public Task<IReadOnlyList<Priority>> ListPrioritiesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Priority>>(new[] { P });

        public Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string description, string color, string icon, int sortOrder, bool isActive, string? inbound, string? outbound, string? inboundFolderId, string? inboundFolderName, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> GetPriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority> CreatePriorityAsync(Priority p, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> UpdatePriorityAsync(Guid id, string name, string slug, int level, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeletePriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Status?> GetStatusAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Status> CreateStatusAsync(Status s, CancellationToken ct) => throw new NotImplementedException();
        public Task<Status?> UpdateStatusAsync(Guid id, string name, string slug, string stateCategory, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteStatusAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Category?> GetCategoryAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Category> CreateCategoryAsync(Category c, CancellationToken ct) => throw new NotImplementedException();
        public Task<Category?> UpdateCategoryAsync(Guid id, Guid? parentId, string name, string slug, string description, int sortOrder, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteCategoryAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubContacts : IContactLookupService
    {
        public CompanyResolution NextResolution { get; set; } = CompanyResolution.None;
        public int ResolveCalls { get; private set; }

        public Task<Contact> EnsureByEmailAsync(string email, string displayName, CancellationToken ct)
            => Task.FromResult(new Contact(Guid.NewGuid(), "Member", displayName, "", email, "", "", true, DateTime.UtcNow, DateTime.UtcNow));

        public Task<CompanyResolution> ResolveCompanyForNewTicketAsync(Guid contactId, CancellationToken ct)
        {
            ResolveCalls++;
            return Task.FromResult(NextResolution);
        }
    }

    private sealed class StubBlobs : IBlobStore
    {
        public Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken ct = default)
        {
            var ms = new MemoryStream();
            content.CopyTo(ms);
            return Task.FromResult(new BlobWriteResult("deadbeef", ms.Length));
        }
        public Task<Stream?> OpenReadAsync(string hash, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public Task<bool> ExistsAsync(string hash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeleteAsync(string hash, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class StubSettings : ISettingsService
    {
        public Task<T> GetAsync<T>(string key, CancellationToken ct)
        {
            object value = key switch
            {
                "Mail.PlusAddressToken" => "TCK",
                _ => default(T)!,
            };
            return Task.FromResult((T)value!);
        }
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopSlaEngine : ISlaEngine
    {
        public Task OnTicketCreatedAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
        public Task OnTicketEventAsync(Guid ticketId, string eventType, CancellationToken ct) => Task.CompletedTask;
        public Task OnTicketFieldsChangedAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
        public Task RecalcAsync(Guid ticketId, CancellationToken ct) => Task.CompletedTask;
    }
}
