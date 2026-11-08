using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Mail.Polling;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class MailPollingServiceTests
{
    [Fact]
    public async Task Success_path_persists_delta_link_and_clears_failures()
    {
        var sourceId = Guid.NewGuid();
        var repo = new InMemoryInboundRepo();
        var graph = new StubGraphClient
        {
            Response = new GraphDeltaPage(
                Messages: new[]
                {
                    new GraphMailSummary("1", "<abc@example>", "Hi", "from@example.com", "F", DateTimeOffset.UtcNow),
                },
                DeltaLink: "https://graph/delta?next=xyz"),
        };

        await MailPollingService.PollSourceCoreAsync(
            sourceId, Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 50, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        var state = await repo.GetAsync(sourceId, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal("https://graph/delta?next=xyz", state!.DeltaLink);
        Assert.Null(state.LastError);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task Failure_path_records_error_and_bumps_consecutive_failures()
    {
        var sourceId = Guid.NewGuid();
        var repo = new InMemoryInboundRepo();
        var graph = new StubGraphClient { Throw = new InvalidOperationException("no tenant") };

        await MailPollingService.PollSourceCoreAsync(
            sourceId, Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 50, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        var state = await repo.GetAsync(sourceId, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(1, state!.ConsecutiveFailures);
        Assert.Contains("no tenant", state.LastError);
    }

    [Fact]
    public async Task Passes_previous_delta_link_back_to_graph_client()
    {
        var sourceId = Guid.NewGuid();
        var repo = new InMemoryInboundRepo();
        await repo.SaveSuccessAsync(sourceId, "https://graph/delta?seed=1", DateTime.UtcNow, default);
        var graph = new StubGraphClient
        {
            Response = new GraphDeltaPage(Array.Empty<GraphMailSummary>(), "https://graph/delta?seed=2"),
        };

        await MailPollingService.PollSourceCoreAsync(
            sourceId, Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 25, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        Assert.Equal("https://graph/delta?seed=1", graph.LastDeltaLink);
        Assert.Equal(25, graph.LastBatchSize);
    }

    [Fact]
    public async Task Skips_source_once_consecutive_failures_exceed_threshold()
    {
        var sourceId = Guid.NewGuid();
        var repo = new InMemoryInboundRepo();
        for (var i = 0; i < 5; i++)
            await repo.SaveFailureAsync(sourceId, "boom", DateTime.UtcNow, default);

        var graph = new StubGraphClient();
        await MailPollingService.PollSourceCoreAsync(
            sourceId, Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 25, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        // Graph was never called because the service skipped.
        Assert.Equal(0, graph.CallCount);
    }

    [Fact]
    public async Task Graph_timeout_is_recorded_as_failure_not_propagated_as_shutdown()
    {
        // An HttpClient timeout surfaces as TaskCanceledException (an
        // OperationCanceledException) while the worker's own token is NOT
        // cancelled. It used to escape and stop the polling loop for good.
        var sourceId = Guid.NewGuid();
        var repo = new InMemoryInboundRepo();
        var graph = new StubGraphClient
        {
            Throw = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
        };

        await MailPollingService.PollSourceCoreAsync(
            sourceId, Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 50, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        var state = await repo.GetAsync(sourceId, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(1, state!.ConsecutiveFailures);
        Assert.Contains("TaskCanceledException", state.LastError);
    }

    [Fact]
    public async Task Real_shutdown_still_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var graph = new StubGraphClient { Throw = new OperationCanceledException(cts.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MailPollingService.PollSourceCoreAsync(
                Guid.NewGuid(), Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 50,
                new InMemoryInboundRepo(), graph, NullLogger.Instance, cts.Token));
    }

    [Fact]
    public async Task Ingest_timeout_on_one_message_does_not_abort_the_cycle()
    {
        var sourceId = Guid.NewGuid();
        var repo = new InMemoryInboundRepo();
        var graph = new StubGraphClient
        {
            Response = new GraphDeltaPage(
                new[] { Summary("1"), Summary("2") }, DeltaLink: "https://graph/delta?next=1"),
        };
        var ingest = new StubIngest(id => id == "1"
            ? throw new TaskCanceledException("timeout")
            : Result(MailIngestOutcome.Created));

        await MailPollingService.PollSourceCoreAsync(
            sourceId, Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 50, repo, graph,
            NullLogger.Instance, CancellationToken.None, ingest, markRead: false,
            notifier: null, finalizer: null);

        Assert.Equal(new[] { "1", "2" }, ingest.Calls);
        var state = await repo.GetAsync(sourceId, CancellationToken.None);
        Assert.Equal("https://graph/delta?next=1", state!.DeltaLink);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task Deliberately_skipped_mail_is_handed_to_the_finalizer_for_moving()
    {
        var sourceId = Guid.NewGuid();
        var graph = new StubGraphClient
        {
            Response = new GraphDeltaPage(
                new[] { Summary("bulk"), Summary("nodefaults"), Summary("created") }, "https://graph/delta?n=1"),
        };
        var ingest = new StubIngest(id => id switch
        {
            "bulk" => Result(MailIngestOutcome.SkippedAutoSubmitted),
            "nodefaults" => Result(MailIngestOutcome.SkippedNoDefaults),
            _ => Result(MailIngestOutcome.Created),
        });
        var finalizer = new StubFinalizer();

        await MailPollingService.PollSourceCoreAsync(
            sourceId, Guid.NewGuid(), "servicedesk", "mailbox@test", "inbox", 50,
            new InMemoryInboundRepo(), graph, NullLogger.Instance, CancellationToken.None,
            ingest, markRead: false, notifier: null, finalizer: finalizer);

        // Only the bulk mail: a config problem stays visible in the inbox and
        // ingested mail goes through the attachment-gated finalize path.
        Assert.Equal(new[] { (sourceId, "mailbox@test", "bulk") }, finalizer.Skipped);
    }

    [Theory]
    [InlineData(MailIngestOutcome.SkippedAutoSubmitted, false, true)]
    [InlineData(MailIngestOutcome.SkippedOwnMailbox, false, true)]
    [InlineData(MailIngestOutcome.SkippedLoop, false, true)]
    [InlineData(MailIngestOutcome.Deduplicated, false, true)]
    [InlineData(MailIngestOutcome.Deduplicated, true, false)]
    [InlineData(MailIngestOutcome.SkippedNoDefaults, false, false)]
    [InlineData(MailIngestOutcome.SkippedNotFound, false, false)]
    [InlineData(MailIngestOutcome.Created, false, false)]
    [InlineData(MailIngestOutcome.Appended, false, false)]
    public void ShouldMoveSkipped_covers_only_final_skip_decisions(
        MailIngestOutcome outcome, bool sameMailboxDuplicate, bool expected)
    {
        var result = Result(outcome) with { DuplicateInSameMailbox = sameMailboxDuplicate };
        Assert.Equal(expected, MailPollingService.ShouldMoveSkipped(result));
    }

    private static GraphMailSummary Summary(string id)
        => new(id, $"<{id}@example>", "Subject", "from@example.com", "F", DateTimeOffset.UtcNow);

    private static MailIngestResult Result(MailIngestOutcome outcome)
        => new(outcome, null, null, null, null);

    private sealed class StubIngest : IMailIngestService
    {
        private readonly Func<string, MailIngestResult> _handler;
        public List<string> Calls { get; } = new();
        public StubIngest(Func<string, MailIngestResult> handler) => _handler = handler;

        public Task<MailIngestResult> IngestAsync(Guid queueId, string queueMailbox, string graphMessageId, CancellationToken ct)
        {
            Calls.Add(graphMessageId);
            return Task.FromResult(_handler(graphMessageId));
        }
    }

    private sealed class StubFinalizer : IMailFinalizer
    {
        public List<(Guid, string, string)> Skipped { get; } = new();
        public Task TryFinalizeMailAsync(Guid mailId, CancellationToken ct) => Task.CompletedTask;
        public Task SweepAsync(CancellationToken ct) => Task.CompletedTask;
        public Task MoveSkippedAsync(Guid sourceId, string mailbox, string graphMessageId, CancellationToken ct)
        {
            Skipped.Add((sourceId, mailbox, graphMessageId));
            return Task.CompletedTask;
        }
    }

    private sealed class StubGraphClient : IGraphMailClient
    {
        public GraphDeltaPage Response { get; set; } =
            new GraphDeltaPage(Array.Empty<GraphMailSummary>(), null);
        public Exception? Throw { get; set; }
        public string? LastDeltaLink { get; private set; }
        public int LastBatchSize { get; private set; }
        public int CallCount { get; private set; }

        public Task<GraphDeltaPage> ListInboxDeltaAsync(
            string mailbox, string folderId, string? deltaLink, int maxPageSize, CancellationToken ct)
        {
            CallCount++;
            LastDeltaLink = deltaLink;
            LastBatchSize = maxPageSize;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Response);
        }

        public Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string mailbox, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GraphMailFolderInfo>>(Array.Empty<GraphMailFolderInfo>());

        public Task<TimeSpan> PingAsync(string mailbox, CancellationToken ct)
            => Task.FromResult(TimeSpan.Zero);

        public Task<GraphFullMessage> FetchMessageAsync(string mailbox, string id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Stream> FetchRawMessageAsync(string mailbox, string id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task MarkAsReadAsync(string mailbox, string id, CancellationToken ct) => Task.CompletedTask;
        public Task MoveAsync(string mailbox, string id, string folderId, CancellationToken ct) => Task.CompletedTask;
        public Task<string> EnsureFolderAsync(string mailbox, string folderName, CancellationToken ct)
            => Task.FromResult("folder-id");
        public Task<Stream> FetchAttachmentBytesAsync(string mailbox, string id, string attachmentId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage m, CancellationToken ct)
            => throw new NotImplementedException();
    }

    // In-memory IQueueInboundMailboxRepository keyed by source id. State writes
    // create-on-write so tests don't need to pre-seed a config row.
    private sealed class InMemoryInboundRepo : IQueueInboundMailboxRepository
    {
        private readonly Dictionary<Guid, QueueInboundMailbox> _map = new();

        private static QueueInboundMailbox Blank(Guid id) => new(
            id, Guid.NewGuid(), "mailbox@test", "inbox", "Inbox", true,
            null, null, null, 0, null, null, null, DateTime.UtcNow, DateTime.UtcNow);

        public Task<QueueInboundMailbox?> GetAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_map.TryGetValue(id, out var s) ? s : null);

        public Task<IReadOnlyList<QueueInboundMailbox>> ListAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<QueueInboundMailbox>>(_map.Values.ToList());

        public Task<IReadOnlyList<QueueInboundMailbox>> ListByQueueAsync(Guid queueId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<QueueInboundMailbox>>(
                _map.Values.Where(s => s.QueueId == queueId).ToList());

        public Task<IReadOnlyList<string>> ListAllMailboxAddressesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(_map.Values.Select(s => s.MailboxAddress).Distinct().ToList());

        public Task<Guid?> FindConflictingQueueAsync(string mailbox, string? folderId, Guid? excludeSourceId, CancellationToken ct)
            => Task.FromResult<Guid?>(null);

        public Task<QueueInboundMailbox> AddAsync(Guid queueId, string mailbox, string? folderId, string? folderName, bool pollingEnabled, CancellationToken ct)
        {
            var row = new QueueInboundMailbox(Guid.NewGuid(), queueId, mailbox, folderId, folderName, pollingEnabled,
                null, null, null, 0, null, null, null, DateTime.UtcNow, DateTime.UtcNow);
            _map[row.Id] = row;
            return Task.FromResult(row);
        }

        public Task<bool> UpdateConfigAsync(Guid id, string mailbox, string? folderId, string? folderName, bool pollingEnabled, CancellationToken ct)
        {
            if (!_map.TryGetValue(id, out var prev)) return Task.FromResult(false);
            _map[id] = prev with { MailboxAddress = mailbox, FolderId = folderId, FolderName = folderName, PollingEnabled = pollingEnabled };
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => Task.FromResult(_map.Remove(id));

        public Task<bool> SetPollingAsync(Guid id, bool enabled, CancellationToken ct)
        {
            if (!_map.TryGetValue(id, out var prev)) return Task.FromResult(false);
            _map[id] = prev with { PollingEnabled = enabled };
            return Task.FromResult(true);
        }

        public Task RefreshMirrorAsync(Guid queueId, CancellationToken ct) => Task.CompletedTask;

        public Task SaveSuccessAsync(Guid id, string? deltaLink, DateTime polledUtc, CancellationToken ct)
        {
            var prev = _map.TryGetValue(id, out var p) ? p : Blank(id);
            _map[id] = prev with { DeltaLink = deltaLink, LastPolledUtc = polledUtc, LastError = null, ConsecutiveFailures = 0, UpdatedUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }

        public Task SaveFailureAsync(Guid id, string error, DateTime polledUtc, CancellationToken ct)
        {
            var prev = _map.TryGetValue(id, out var p) ? p : Blank(id);
            _map[id] = prev with { LastPolledUtc = polledUtc, LastError = error, ConsecutiveFailures = prev.ConsecutiveFailures + 1, UpdatedUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }

        public Task ResetFailuresAsync(Guid id, CancellationToken ct)
        {
            if (_map.TryGetValue(id, out var prev))
                _map[id] = prev with { LastError = null, ConsecutiveFailures = 0, LastMailboxActionError = null, LastMailboxActionErrorUtc = null, UpdatedUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }

        public Task SaveProcessedFolderIdAsync(Guid id, string folderId, CancellationToken ct)
        {
            var prev = _map.TryGetValue(id, out var p) ? p : Blank(id);
            _map[id] = prev with { ProcessedFolderId = folderId, UpdatedUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }

        public Task SaveMailboxActionErrorAsync(Guid id, string error, DateTime occurredUtc, CancellationToken ct)
        {
            var prev = _map.TryGetValue(id, out var p) ? p : Blank(id);
            _map[id] = prev with { LastMailboxActionError = error, LastMailboxActionErrorUtc = occurredUtc, UpdatedUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }

        public Task ClearMailboxActionErrorAsync(Guid id, CancellationToken ct)
        {
            if (_map.TryGetValue(id, out var prev))
                _map[id] = prev with { LastMailboxActionError = null, LastMailboxActionErrorUtc = null, UpdatedUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }
    }
}
