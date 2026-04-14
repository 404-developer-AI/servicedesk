using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Secrets;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class HealthAggregatorTests
{
    [Fact]
    public async Task No_queues_with_mailbox_reports_ok_with_idle_summary()
    {
        var agg = Build(queues: new List<Queue>(), states: new List<MailPollState>(), hasSecret: false);

        var report = await agg.CollectAsync(CancellationToken.None);

        var mail = report.Subsystems.Single(s => s.Key == "mail-polling");
        Assert.Equal(HealthStatus.Ok, mail.Status);
        Assert.Contains("No queues", mail.Summary);
    }

    [Fact]
    public async Task Five_consecutive_failures_reports_critical_and_surfaces_reset_action()
    {
        var queueId = Guid.NewGuid();
        var agg = Build(
            queues: new[] { MakeQueue(queueId, "servicedesk", "inbox@test") },
            states: new[] { MakeState(queueId, failures: 5, error: "token expired") },
            hasSecret: true);

        var report = await agg.CollectAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Critical, report.Status);
        var mail = report.Subsystems.Single(s => s.Key == "mail-polling");
        Assert.Equal(HealthStatus.Critical, mail.Status);
        var action = Assert.Single(mail.Actions);
        Assert.Contains(queueId.ToString(), action.Endpoint);
        Assert.Contains("servicedesk", action.Label);
    }

    [Fact]
    public async Task Partial_failures_report_warning_without_critical_rollup()
    {
        var queueId = Guid.NewGuid();
        var agg = Build(
            queues: new[] { MakeQueue(queueId, "ops", "ops@test") },
            states: new[] { MakeState(queueId, failures: 2, error: "timeout") },
            hasSecret: true);

        var report = await agg.CollectAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Warning, report.Status);
        Assert.Empty(report.Subsystems.Single(s => s.Key == "mail-polling").Actions);
    }

    [Fact]
    public async Task Missing_graph_secret_reports_warning()
    {
        var agg = Build(queues: new List<Queue>(), states: new List<MailPollState>(), hasSecret: false);

        var report = await agg.CollectAsync(CancellationToken.None);

        var graph = report.Subsystems.Single(s => s.Key == "graph-auth");
        Assert.Equal(HealthStatus.Warning, graph.Status);
    }

    private static HealthAggregator Build(
        IReadOnlyList<Queue> queues,
        IReadOnlyList<MailPollState> states,
        bool hasSecret)
        => new(new StubPollStateRepo(states), new StubTaxonomyRepo(queues), new StubSecretStore(hasSecret));

    private static Queue MakeQueue(Guid id, string name, string mailbox) => new(
        Id: id, Name: name, Slug: name, Description: "", Color: "#fff", Icon: "",
        SortOrder: 0, IsActive: true, IsSystem: false,
        CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow,
        InboundMailboxAddress: mailbox, OutboundMailboxAddress: null);

    private static MailPollState MakeState(Guid queueId, int failures, string? error) => new(
        queueId, DeltaLink: null, LastPolledUtc: DateTime.UtcNow,
        LastError: error, ConsecutiveFailures: failures, UpdatedUtc: DateTime.UtcNow);

    private sealed class StubPollStateRepo : IMailPollStateRepository
    {
        private readonly IReadOnlyList<MailPollState> _states;
        public StubPollStateRepo(IReadOnlyList<MailPollState> states) => _states = states;
        public Task<IReadOnlyList<MailPollState>> ListAllAsync(CancellationToken ct) => Task.FromResult(_states);
        public Task<MailPollState?> GetAsync(Guid queueId, CancellationToken ct)
            => Task.FromResult(_states.FirstOrDefault(s => s.QueueId == queueId));
        public Task SaveSuccessAsync(Guid queueId, string? deltaLink, DateTime polledUtc, CancellationToken ct) => Task.CompletedTask;
        public Task SaveFailureAsync(Guid queueId, string error, DateTime polledUtc, CancellationToken ct) => Task.CompletedTask;
        public Task ResetFailuresAsync(Guid queueId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubSecretStore : IProtectedSecretStore
    {
        private readonly bool _has;
        public StubSecretStore(bool has) => _has = has;
        public Task<bool> HasAsync(string key, CancellationToken ct) => Task.FromResult(_has);
        public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult<string?>(_has ? "secret" : null);
        public Task SetAsync(string key, string value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubTaxonomyRepo : ITaxonomyRepository
    {
        private readonly IReadOnlyList<Queue> _queues;
        public StubTaxonomyRepo(IReadOnlyList<Queue> queues) => _queues = queues;
        public Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct) => Task.FromResult(_queues);

        public Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string description, string color, string icon, int sortOrder, bool isActive, string? inboundMailboxAddress, string? outboundMailboxAddress, CancellationToken ct) => throw new NotImplementedException();
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
}
