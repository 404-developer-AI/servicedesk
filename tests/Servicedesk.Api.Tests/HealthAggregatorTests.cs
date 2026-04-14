using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Mail.Polling;
using Servicedesk.Infrastructure.Observability;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Storage;
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
    public async Task Mailbox_action_error_reports_warning_without_pausing()
    {
        var queueId = Guid.NewGuid();
        var state = new MailPollState(
            queueId, DeltaLink: "x", LastPolledUtc: DateTime.UtcNow,
            LastError: null, ConsecutiveFailures: 0, UpdatedUtc: DateTime.UtcNow,
            ProcessedFolderId: null,
            LastMailboxActionError: "mark-as-read: Access is denied",
            LastMailboxActionErrorUtc: DateTime.UtcNow);
        var agg = Build(
            queues: new[] { MakeQueue(queueId, "desk", "inbox@test") },
            states: new[] { state },
            hasSecret: true);

        var report = await agg.CollectAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Warning, report.Status);
        var mail = report.Subsystems.Single(s => s.Key == "mail-polling");
        Assert.Equal(HealthStatus.Warning, mail.Status);
        Assert.Empty(mail.Actions); // no reset needed — poller is still working
        Assert.Contains(mail.Details, d => d.Value.Contains("Mail.ReadWrite"));
    }

    [Fact]
    public async Task Attachment_backlog_reports_warning_without_action()
    {
        var agg = Build(new List<Queue>(), new List<MailPollState>(), hasSecret: true,
            backlog: 4, deadLetters: 0);

        var report = await agg.CollectAsync(CancellationToken.None);

        var sub = report.Subsystems.Single(s => s.Key == "attachment-jobs");
        Assert.Equal(HealthStatus.Warning, sub.Status);
        Assert.Empty(sub.Actions);
        Assert.Contains(sub.Details, d => d.Value.Contains("4"));
    }

    [Fact]
    public async Task Attachment_dead_letters_report_critical_with_requeue_action()
    {
        var agg = Build(new List<Queue>(), new List<MailPollState>(), hasSecret: true,
            backlog: 0, deadLetters: 2);

        var report = await agg.CollectAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Critical, report.Status);
        var sub = report.Subsystems.Single(s => s.Key == "attachment-jobs");
        Assert.Equal(HealthStatus.Critical, sub.Status);
        Assert.Equal(2, sub.Actions.Count);
        Assert.Contains(sub.Actions, a => a.Endpoint.Contains("requeue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sub.Actions, a => a.Endpoint.Contains("cancel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Blob_store_single_failure_reports_warning_with_clear_action()
    {
        var blob = new BlobStoreHealth();
        blob.RecordFailure("write", new IOException("path syntax"));
        var agg = Build(new List<Queue>(), new List<MailPollState>(), hasSecret: true, blobHealth: blob);

        var report = await agg.CollectAsync(CancellationToken.None);

        var sub = report.Subsystems.Single(s => s.Key == "blob-store");
        Assert.Equal(HealthStatus.Warning, sub.Status);
        var action = Assert.Single(sub.Actions);
        Assert.Contains("clear", action.Endpoint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Blob_store_three_consecutive_failures_reports_critical()
    {
        var blob = new BlobStoreHealth();
        blob.RecordFailure("write", new IOException("disk full"));
        blob.RecordFailure("write", new IOException("disk full"));
        blob.RecordFailure("write", new IOException("disk full"));
        var agg = Build(new List<Queue>(), new List<MailPollState>(), hasSecret: true, blobHealth: blob);

        var report = await agg.CollectAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Critical, report.Status);
        var sub = report.Subsystems.Single(s => s.Key == "blob-store");
        Assert.Equal(HealthStatus.Critical, sub.Status);
    }

    [Fact]
    public async Task Blob_store_success_after_failure_returns_to_ok()
    {
        var blob = new BlobStoreHealth();
        blob.RecordFailure("write", new IOException("x"));
        blob.RecordSuccess();
        var agg = Build(new List<Queue>(), new List<MailPollState>(), hasSecret: true, blobHealth: blob);

        var report = await agg.CollectAsync(CancellationToken.None);

        var sub = report.Subsystems.Single(s => s.Key == "blob-store");
        Assert.Equal(HealthStatus.Ok, sub.Status);
        Assert.Empty(sub.Actions);
    }

    [Fact]
    public async Task Open_incident_warning_bumps_subsystem_to_warning()
    {
        var agg = Build(new List<Queue>(), new List<MailPollState>(), hasSecret: true,
            openIncidents: new Dictionary<string, IncidentSeverity> { ["mail-polling"] = IncidentSeverity.Warning });

        var report = await agg.CollectAsync(CancellationToken.None);

        var sub = report.Subsystems.Single(s => s.Key == "mail-polling");
        Assert.Equal(HealthStatus.Warning, sub.Status);
        Assert.Contains(sub.Details, d => d.Label == "Unacknowledged incidents");
    }

    [Fact]
    public async Task Open_incident_critical_bumps_subsystem_to_critical()
    {
        var agg = Build(new List<Queue>(), new List<MailPollState>(), hasSecret: true,
            openIncidents: new Dictionary<string, IncidentSeverity> { ["attachment-jobs"] = IncidentSeverity.Critical });

        var report = await agg.CollectAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Critical, report.Status);
        var sub = report.Subsystems.Single(s => s.Key == "attachment-jobs");
        Assert.Equal(HealthStatus.Critical, sub.Status);
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
        bool hasSecret,
        int backlog = 0,
        int deadLetters = 0,
        IBlobStoreHealth? blobHealth = null,
        IReadOnlyDictionary<string, IncidentSeverity>? openIncidents = null)
        => new(
            new StubPollStateRepo(states),
            new StubTaxonomyRepo(queues),
            new StubSecretStore(hasSecret),
            new StubAttachmentJobsRepo(backlog, deadLetters),
            blobHealth ?? new BlobStoreHealth(),
            new StubIncidentLog(openIncidents ?? new Dictionary<string, IncidentSeverity>()));

    private sealed class StubIncidentLog : IIncidentLog
    {
        private readonly IReadOnlyDictionary<string, IncidentSeverity> _open;
        public StubIncidentLog(IReadOnlyDictionary<string, IncidentSeverity> open) => _open = open;
        public Task ReportAsync(string subsystem, IncidentSeverity severity, string message, string? details, string? contextJson, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<IncidentRow>> ListOpenAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IncidentRow>>(Array.Empty<IncidentRow>());
        public Task<IReadOnlyList<IncidentRow>> ListOpenRecentAsync(int take, CancellationToken ct) => Task.FromResult<IReadOnlyList<IncidentRow>>(Array.Empty<IncidentRow>());
        public Task<IReadOnlyList<IncidentRow>> ListArchiveAsync(string? subsystem, int take, int skip, CancellationToken ct) => Task.FromResult<IReadOnlyList<IncidentRow>>(Array.Empty<IncidentRow>());
        public Task<string?> AcknowledgeAsync(long id, Guid userId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<int> AcknowledgeSubsystemAsync(string subsystem, Guid userId, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyDictionary<string, IncidentSeverity>> GetOpenBySubsystemAsync(CancellationToken ct) => Task.FromResult(_open);
    }

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
        public Task SaveProcessedFolderIdAsync(Guid queueId, string folderId, CancellationToken ct) => Task.CompletedTask;
        public Task SaveMailboxActionErrorAsync(Guid queueId, string error, DateTime occurredUtc, CancellationToken ct) => Task.CompletedTask;
        public Task ClearMailboxActionErrorAsync(Guid queueId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubAttachmentJobsRepo : IAttachmentJobRepository
    {
        private readonly int _backlog;
        private readonly int _deadLetters;
        public StubAttachmentJobsRepo(int backlog, int deadLetters)
        {
            _backlog = backlog;
            _deadLetters = deadLetters;
        }
        public Task<int> CountPendingOlderThanAsync(TimeSpan threshold, DateTime nowUtc, CancellationToken ct) => Task.FromResult(_backlog);
        public Task<int> CountDeadLetteredAsync(CancellationToken ct) => Task.FromResult(_deadLetters);
        public Task<AttachmentJobClaim?> ClaimNextAsync(DateTime nowUtc, CancellationToken ct) => Task.FromResult<AttachmentJobClaim?>(null);
        public Task CompleteAsync(long jobId, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task ScheduleRetryAsync(long jobId, DateTime nextAttemptUtc, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task DeadLetterAsync(long jobId, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task<int> RequeueDeadLetteredAsync(DateTime nowUtc, CancellationToken ct) => Task.FromResult(_deadLetters);
        public Task<int> CancelDeadLetteredAsync(CancellationToken ct) => Task.FromResult(_deadLetters);
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
