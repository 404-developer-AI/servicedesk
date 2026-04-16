using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Mail.Polling;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class MailPollingServiceTests
{
    [Fact]
    public async Task Success_path_persists_delta_link_and_clears_failures()
    {
        var queueId = Guid.NewGuid();
        var repo = new InMemoryPollStateRepo();
        var graph = new StubGraphClient
        {
            Response = new GraphDeltaPage(
                Messages: new[]
                {
                    new GraphMailSummary("1", "<abc@example>", "Hi", "from@example.com", "F", DateTimeOffset.UtcNow),
                },
                DeltaLink: "https://graph/delta?next=xyz"),
        };

        await MailPollingService.PollQueueCoreAsync(
            queueId, "servicedesk", "mailbox@test", "inbox", 50, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        var state = await repo.GetAsync(queueId, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal("https://graph/delta?next=xyz", state!.DeltaLink);
        Assert.Null(state.LastError);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task Failure_path_records_error_and_bumps_consecutive_failures()
    {
        var queueId = Guid.NewGuid();
        var repo = new InMemoryPollStateRepo();
        var graph = new StubGraphClient { Throw = new InvalidOperationException("no tenant") };

        await MailPollingService.PollQueueCoreAsync(
            queueId, "servicedesk", "mailbox@test", "inbox", 50, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        var state = await repo.GetAsync(queueId, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(1, state!.ConsecutiveFailures);
        Assert.Contains("no tenant", state.LastError);
    }

    [Fact]
    public async Task Passes_previous_delta_link_back_to_graph_client()
    {
        var queueId = Guid.NewGuid();
        var repo = new InMemoryPollStateRepo();
        await repo.SaveSuccessAsync(queueId, "https://graph/delta?seed=1", DateTime.UtcNow, default);
        var graph = new StubGraphClient
        {
            Response = new GraphDeltaPage(Array.Empty<GraphMailSummary>(), "https://graph/delta?seed=2"),
        };

        await MailPollingService.PollQueueCoreAsync(
            queueId, "servicedesk", "mailbox@test", "inbox", 25, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        Assert.Equal("https://graph/delta?seed=1", graph.LastDeltaLink);
        Assert.Equal(25, graph.LastBatchSize);
    }

    [Fact]
    public async Task Skips_queue_once_consecutive_failures_exceed_threshold()
    {
        var queueId = Guid.NewGuid();
        var repo = new InMemoryPollStateRepo();
        for (var i = 0; i < 5; i++)
            await repo.SaveFailureAsync(queueId, "boom", DateTime.UtcNow, default);

        var graph = new StubGraphClient();
        await MailPollingService.PollQueueCoreAsync(
            queueId, "servicedesk", "mailbox@test", "inbox", 25, repo, graph,
            NullLogger.Instance, CancellationToken.None);

        // Graph was never called because the service skipped.
        Assert.Equal(0, graph.CallCount);
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
    }

    private sealed class InMemoryPollStateRepo : IMailPollStateRepository
    {
        private readonly Dictionary<Guid, MailPollState> _map = new();

        public Task<MailPollState?> GetAsync(Guid queueId, CancellationToken ct)
            => Task.FromResult(_map.TryGetValue(queueId, out var s) ? s : null);

        public Task SaveSuccessAsync(Guid queueId, string? deltaLink, DateTime polledUtc, CancellationToken ct)
        {
            _map[queueId] = new MailPollState(queueId, deltaLink, polledUtc, null, 0, DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task SaveFailureAsync(Guid queueId, string error, DateTime polledUtc, CancellationToken ct)
        {
            _map.TryGetValue(queueId, out var prev);
            _map[queueId] = new MailPollState(
                queueId, prev?.DeltaLink, polledUtc, error,
                (prev?.ConsecutiveFailures ?? 0) + 1, DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MailPollState>> ListAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailPollState>>(_map.Values.ToList());

        public Task ResetFailuresAsync(Guid queueId, CancellationToken ct)
        {
            if (_map.TryGetValue(queueId, out var prev))
            {
                _map[queueId] = prev with { LastError = null, ConsecutiveFailures = 0, UpdatedUtc = DateTime.UtcNow };
            }
            return Task.CompletedTask;
        }

        public Task SaveMailboxActionErrorAsync(Guid queueId, string error, DateTime occurredUtc, CancellationToken ct)
        {
            if (_map.TryGetValue(queueId, out var prev))
                _map[queueId] = prev with { LastMailboxActionError = error, LastMailboxActionErrorUtc = occurredUtc, UpdatedUtc = DateTime.UtcNow };
            else
                _map[queueId] = new MailPollState(queueId, null, null, null, 0, DateTime.UtcNow, null, error, occurredUtc);
            return Task.CompletedTask;
        }

        public Task ClearMailboxActionErrorAsync(Guid queueId, CancellationToken ct)
        {
            if (_map.TryGetValue(queueId, out var prev))
                _map[queueId] = prev with { LastMailboxActionError = null, LastMailboxActionErrorUtc = null, UpdatedUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }

        public Task SaveProcessedFolderIdAsync(Guid queueId, string folderId, CancellationToken ct)
        {
            if (_map.TryGetValue(queueId, out var prev))
            {
                _map[queueId] = prev with { ProcessedFolderId = folderId, UpdatedUtc = DateTime.UtcNow };
            }
            else
            {
                _map[queueId] = new MailPollState(queueId, null, null, null, 0, DateTime.UtcNow, folderId);
            }
            return Task.CompletedTask;
        }
    }
}
