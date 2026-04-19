using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

public class MailFinalizerTests
{
    private static readonly Guid QueueId = Guid.NewGuid();
    private const string Mailbox = "ingest@example.com";

    [Fact]
    public async Task Sweep_moves_candidates_and_marks_moved()
    {
        var mailId = Guid.NewGuid();
        var mail = new FakeMailRepo
        {
            Ready = new[] { new FinalizeCandidate(mailId, "graph-msg-1", Mailbox) },
        };
        var graph = new FakeGraph();
        var finalizer = new MailFinalizer(mail, graph, new FakePollState(),
            new FakeTaxonomy(Mailbox), new FakeSettings(doMove: true),
            NullLogger<MailFinalizer>.Instance);

        await finalizer.SweepAsync(CancellationToken.None);

        Assert.Single(graph.Moves);
        Assert.Equal(("graph-msg-1", "folder-Processed"), graph.Moves[0]);
        Assert.Contains(mailId, mail.Moved);
    }

    [Fact]
    public async Task Sweep_skips_when_move_setting_disabled()
    {
        var mail = new FakeMailRepo
        {
            Ready = new[] { new FinalizeCandidate(Guid.NewGuid(), "g", Mailbox) },
        };
        var graph = new FakeGraph();
        var finalizer = new MailFinalizer(mail, graph, new FakePollState(),
            new FakeTaxonomy(Mailbox), new FakeSettings(doMove: false),
            NullLogger<MailFinalizer>.Instance);

        await finalizer.SweepAsync(CancellationToken.None);

        Assert.Empty(graph.Moves);
        Assert.Empty(mail.Moved);
    }

    [Fact]
    public async Task TryFinalize_no_op_when_not_ready()
    {
        var mail = new FakeMailRepo(); // empty Ready → Get returns null
        var graph = new FakeGraph();
        var finalizer = new MailFinalizer(mail, graph, new FakePollState(),
            new FakeTaxonomy(Mailbox), new FakeSettings(doMove: true),
            NullLogger<MailFinalizer>.Instance);

        await finalizer.TryFinalizeMailAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(graph.Moves);
        Assert.Empty(mail.Moved);
    }

    [Fact]
    public async Task Graph_404_still_marks_moved_to_stop_retries()
    {
        var mailId = Guid.NewGuid();
        var mail = new FakeMailRepo
        {
            Ready = new[] { new FinalizeCandidate(mailId, "gone", Mailbox) },
        };
        var graph = new FakeGraph { ThrowNotFound = true };
        var finalizer = new MailFinalizer(mail, graph, new FakePollState(),
            new FakeTaxonomy(Mailbox), new FakeSettings(doMove: true),
            NullLogger<MailFinalizer>.Instance);

        await finalizer.SweepAsync(CancellationToken.None);

        Assert.Contains(mailId, mail.Moved);
    }

    [Fact]
    public async Task Unknown_mailbox_marks_moved_and_skips_graph()
    {
        var mailId = Guid.NewGuid();
        var mail = new FakeMailRepo
        {
            Ready = new[] { new FinalizeCandidate(mailId, "g", "unknown@example.com") },
        };
        var graph = new FakeGraph();
        var finalizer = new MailFinalizer(mail, graph, new FakePollState(),
            new FakeTaxonomy(Mailbox), new FakeSettings(doMove: true),
            NullLogger<MailFinalizer>.Instance);

        await finalizer.SweepAsync(CancellationToken.None);

        Assert.Empty(graph.Moves);
        Assert.Contains(mailId, mail.Moved);
    }

    // ---- stubs: only the methods MailFinalizer actually touches are ----
    // ---- meaningfully implemented; everything else throws if called. ----

    private sealed class FakeMailRepo : IMailMessageRepository
    {
        public IReadOnlyList<FinalizeCandidate> Ready { get; set; } = Array.Empty<FinalizeCandidate>();
        public HashSet<Guid> Moved { get; } = new();

        public Task<IReadOnlyList<FinalizeCandidate>> ListReadyForFinalizeAsync(int limit, CancellationToken ct)
            => Task.FromResult(Ready);
        public Task<FinalizeCandidate?> GetIfReadyForFinalizeAsync(Guid mailId, CancellationToken ct)
            => Task.FromResult(Ready.FirstOrDefault(c => c.MailId == mailId));
        public Task MarkMailboxMovedAsync(Guid mailId, DateTime utc, CancellationToken ct)
        {
            Moved.Add(mailId);
            return Task.CompletedTask;
        }

        public Task<MailMessageRow?> GetByMessageIdAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<MailMessageRow?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid?> FindTicketIdByReferencesAsync(IReadOnlyList<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> InsertAsync(NewMailMessage row, IReadOnlyList<NewMailRecipient> r, IReadOnlyList<NewMailAttachment> a, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> InsertOutboundAsync(NewOutboundMailMessage row, IReadOnlyList<NewMailRecipient> r, CancellationToken ct) => throw new NotImplementedException();
        public Task<MailThreadAnchor?> GetLatestThreadAnchorAsync(Guid ticketId, CancellationToken ct) => Task.FromResult<MailThreadAnchor?>(null);
        public Task<IReadOnlyList<MailRecipientRow>> ListRecipientsAsync(Guid mailId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MailRecipientRow>>(Array.Empty<MailRecipientRow>());
        public Task AttachToTicketAsync(Guid mailId, Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeGraph : IGraphMailClient
    {
        public List<(string MessageId, string FolderId)> Moves { get; } = new();
        public bool ThrowNotFound { get; set; }

        public Task MoveAsync(string mailbox, string id, string folderId, CancellationToken ct)
        {
            if (ThrowNotFound)
            {
                var err = new Microsoft.Graph.Models.ODataErrors.ODataError
                {
                    ResponseStatusCode = 404,
                    Error = new Microsoft.Graph.Models.ODataErrors.MainError { Code = "ErrorItemNotFound" },
                };
                throw err;
            }
            Moves.Add((id, folderId));
            return Task.CompletedTask;
        }

        public Task<string> EnsureFolderAsync(string mailbox, string folderName, CancellationToken ct)
            => Task.FromResult("folder-" + folderName);

        public Task<GraphDeltaPage> ListInboxDeltaAsync(string m, string f, string? d, int b, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphMailFolderInfo>>(Array.Empty<GraphMailFolderInfo>());
        public Task<TimeSpan> PingAsync(string m, CancellationToken ct) => throw new NotImplementedException();
        public Task<GraphFullMessage> FetchMessageAsync(string m, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Stream> FetchRawMessageAsync(string m, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task MarkAsReadAsync(string m, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Stream> FetchAttachmentBytesAsync(string m, string id, string aid, CancellationToken ct) => throw new NotImplementedException();
        public Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage m, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakePollState : IMailPollStateRepository
    {
        public Dictionary<Guid, string> FolderIds { get; } = new();
        public Task<MailPollState?> GetAsync(Guid queueId, CancellationToken ct)
            => Task.FromResult<MailPollState?>(FolderIds.TryGetValue(queueId, out var f)
                ? new MailPollState(queueId, null, null, null, 0, DateTime.UtcNow, ProcessedFolderId: f)
                : null);
        public Task<IReadOnlyList<MailPollState>> ListAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailPollState>>(Array.Empty<MailPollState>());
        public Task SaveSuccessAsync(Guid q, string? d, DateTime t, CancellationToken ct) => Task.CompletedTask;
        public Task SaveFailureAsync(Guid q, string e, DateTime t, CancellationToken ct) => Task.CompletedTask;
        public Task ResetFailuresAsync(Guid q, CancellationToken ct) => Task.CompletedTask;
        public Task SaveProcessedFolderIdAsync(Guid q, string f, CancellationToken ct) { FolderIds[q] = f; return Task.CompletedTask; }
        public Task SaveMailboxActionErrorAsync(Guid q, string e, DateTime t, CancellationToken ct) => Task.CompletedTask;
        public Task ClearMailboxActionErrorAsync(Guid q, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeTaxonomy : ITaxonomyRepository
    {
        private readonly Queue _queue;
        public FakeTaxonomy(string mailbox)
        {
            _queue = new Queue(QueueId, "ingest", "ingest", "", "#fff", "inbox",
                0, true, false, DateTime.UtcNow, DateTime.UtcNow, mailbox, null);
        }
        public Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Queue>>(new[] { _queue });

        public Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string desc, string color, string icon, int sortOrder, bool isActive, string? inbound, string? outbound, string? inboundFolderId, string? inboundFolderName, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Priority>> ListPrioritiesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> GetPriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority> CreatePriorityAsync(Priority p, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> UpdatePriorityAsync(Guid id, string name, string slug, int level, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeletePriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Status>> ListStatusesAsync(CancellationToken ct) => throw new NotImplementedException();
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

    private sealed class FakeSettings : ISettingsService
    {
        private readonly bool _doMove;
        private readonly string _folder;
        public FakeSettings(bool doMove, string folder = "Processed") { _doMove = doMove; _folder = folder; }
        public Task<T> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (key == SettingKeys.Mail.MoveOnIngest && typeof(T) == typeof(bool))
                return Task.FromResult((T)(object)_doMove);
            if (key == SettingKeys.Mail.ProcessedFolderName && typeof(T) == typeof(string))
                return Task.FromResult((T)(object)_folder);
            return Task.FromResult(default(T)!);
        }
        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }
}
