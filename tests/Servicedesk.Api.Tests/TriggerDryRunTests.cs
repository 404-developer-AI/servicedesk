using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Templating;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Tests for v0.0.24 Blok 7 — TriggerService.DryRunAsync. The shape we
/// care about: matcher + render-context run normally, the preview
/// dispatcher is called, and NOTHING is persisted (no trigger_runs row,
/// no audit event). The endpoint above this layer just projects the
/// returned record into JSON.
public class TriggerDryRunTests
{
    [Fact]
    public async Task Returns_null_when_trigger_not_found()
    {
        var repo = new FakeRepo();
        var service = Build(repo, new FakePreview(), new FakeMatcher(true), new FakeTickets());

        var result = await service.DryRunAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(repo.Recorded);
        Assert.Equal(0, repo.AuditCalls);
    }

    [Fact]
    public async Task Returns_null_when_ticket_not_found()
    {
        var trigger = MakeTrigger();
        var repo = new FakeRepo(trigger);
        var service = Build(repo, new FakePreview(), new FakeMatcher(true), new FakeTickets());

        var result = await service.DryRunAsync(trigger.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(repo.Recorded);
    }

    [Fact]
    public async Task Matcher_false_returns_unmatched_with_no_actions()
    {
        var ticketId = Guid.NewGuid();
        var trigger = MakeTrigger();
        var repo = new FakeRepo(trigger);
        var preview = new FakePreview();
        var service = Build(repo, preview, new FakeMatcher(false), new FakeTickets(SeedTicket(ticketId)));

        var result = await service.DryRunAsync(trigger.Id, ticketId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Matched);
        Assert.Null(result.FailureReason);
        Assert.Empty(result.Actions);
        Assert.Equal(0, preview.Calls);
        // Critical: dry-run NEVER writes trigger_runs or audit, regardless
        // of outcome. A matcher-false case is the easiest to assert this on
        // since the production path also writes a SkippedNoMatch row there.
        Assert.Empty(repo.Recorded);
        Assert.Equal(0, repo.AuditCalls);
    }

    [Fact]
    public async Task Matcher_true_returns_preview_results_without_persisting()
    {
        var ticketId = Guid.NewGuid();
        var trigger = MakeTrigger(actionsJson: "[{\"kind\":\"set_priority\",\"priority_id\":\"" + Guid.NewGuid() + "\"}]");
        var repo = new FakeRepo(trigger);
        var preview = new FakePreview(new[]
        {
            TriggerActionPreviewResult.WouldApply("set_priority", new { from = "low", to = "high" }),
        });
        var service = Build(repo, preview, new FakeMatcher(true), new FakeTickets(SeedTicket(ticketId)));

        var result = await service.DryRunAsync(trigger.Id, ticketId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Matched);
        var action = Assert.Single(result.Actions);
        Assert.Equal(TriggerActionPreviewStatus.WouldApply, action.Status);
        Assert.Equal("set_priority", action.Kind);
        Assert.Equal(1, preview.Calls);
        Assert.Empty(repo.Recorded);
        Assert.Equal(0, repo.AuditCalls);
    }

    [Fact]
    public async Task Invalid_conditions_json_returns_failure_reason()
    {
        var ticketId = Guid.NewGuid();
        var trigger = MakeTrigger(conditionsJson: "{not valid json");
        var repo = new FakeRepo(trigger);
        var service = Build(repo, new FakePreview(), new FakeMatcher(true), new FakeTickets(SeedTicket(ticketId)));

        var result = await service.DryRunAsync(trigger.Id, ticketId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Matched);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("invalid JSON", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Actions);
        Assert.Empty(repo.Recorded);
    }

    // ----- helpers + fakes (single-purpose: only what DryRunAsync touches) -----

    private static TriggerService Build(FakeRepo repo, FakePreview preview, FakeMatcher matcher, FakeTickets tickets)
        => new TriggerService(
            repo,
            tickets,
            matcher,
            new ThrowingDispatcher(),
            preview,
            new TriggerLoopGuard(),
            new FakeSettings(),
            new ThrowingAudit(),
            new FakeRenderFactory(),
            new Servicedesk.Infrastructure.Realtime.NullTicketListNotifier(),
            NullLogger<TriggerService>.Instance);

    private static TriggerRow MakeTrigger(string conditionsJson = "{\"op\":\"AND\",\"items\":[]}", string actionsJson = "[]")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Description = string.Empty,
            IsActive = true,
            ActivatorKind = "action",
            ActivatorMode = "always",
            ConditionsJson = conditionsJson,
            ActionsJson = actionsJson,
            Locale = null,
            Timezone = null,
            Note = string.Empty,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            CreatedByUserId = null,
        };

    private static TicketDetail SeedTicket(Guid id)
    {
        var t = new Ticket(
            Id: id,
            Number: 42,
            Subject: "X",
            RequesterContactId: Guid.NewGuid(),
            AssigneeUserId: null,
            QueueId: Guid.NewGuid(),
            StatusId: Guid.NewGuid(),
            PriorityId: Guid.NewGuid(),
            CategoryId: null,
            Source: "Web",
            ExternalRef: null,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow,
            DueUtc: null,
            FirstResponseUtc: null,
            ResolvedUtc: null,
            ClosedUtc: null,
            IsDeleted: false);
        return new TicketDetail(t, new TicketBody(id, string.Empty, null), Array.Empty<TicketEvent>(), Array.Empty<TicketEventPin>());
    }

    private sealed class FakeRepo : ITriggerRepository
    {
        private readonly TriggerRow? _row;
        public List<TriggerRunRecord> Recorded { get; } = new();
        public int AuditCalls { get; private set; }
        public FakeRepo() => _row = null;
        public FakeRepo(TriggerRow row) => _row = row;
        public Task<TriggerRow?> GetByIdAsync(Guid triggerId, CancellationToken ct)
            => Task.FromResult(_row is not null && _row.Id == triggerId ? _row : null);
        public Task RecordRunAsync(TriggerRunRecord record, CancellationToken ct)
        {
            Recorded.Add(record);
            return Task.CompletedTask;
        }

        // ----- everything else: throw to flag accidental calls -----
        public Task<IReadOnlyList<TriggerRow>> LoadActiveAsync(TriggerActivatorKind activatorKind, CancellationToken ct) => throw new InvalidOperationException("DryRun should not load by activator kind.");
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListReminderCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationWarningCandidatesAsync(int warningMinutes, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRow>> ListAllAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow> CreateAsync(NewTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow?> UpdateAsync(Guid id, UpdateTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TriggerRunSummary>> GetRunSummariesAsync(DateTime sinceUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRunDetail>> ListRunsAsync(Guid triggerId, int limit, DateTime? cursorUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakePreview : ITriggerActionPreviewDispatcher
    {
        private readonly IReadOnlyList<TriggerActionPreviewResult> _results;
        public int Calls { get; private set; }
        public FakePreview() : this(Array.Empty<TriggerActionPreviewResult>()) { }
        public FakePreview(IEnumerable<TriggerActionPreviewResult> results) => _results = results.ToList();
        public Task<IReadOnlyList<TriggerActionPreviewResult>> PreviewAllAsync(JsonElement actions, TriggerEvaluationContext ctx, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_results);
        }
    }

    private sealed class FakeMatcher : ITriggerConditionMatcher
    {
        private readonly bool _matches;
        public FakeMatcher(bool matches) => _matches = matches;
        public bool Matches(JsonElement conditions, TriggerEvaluationContext ctx) => _matches;
        public IReadOnlySet<string> ReferencedFields(JsonElement conditions)
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeTickets : ITicketRepository
    {
        private readonly TicketDetail? _detail;
        public FakeTickets() => _detail = null;
        public FakeTickets(TicketDetail detail) => _detail = detail;
        public Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_detail is not null && _detail.Ticket.Id == id ? _detail : null);

        public Task<TicketPage> SearchAsync(TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> AssignCompanyAsync(Guid ticketId, Guid companyId, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> ChangeRequesterAsync(Guid ticketId, Guid newContactId, Guid? newCompanyId, bool awaitingCompanyAssignment, string? companyResolvedVia, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEvent?> UpdateEventAsync(Guid ticketId, long eventId, UpdateTicketEvent input, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> PinEventAsync(Guid ticketId, long eventId, Guid userId, string remark, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UnpinEventAsync(Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> UpdatePinRemarkAsync(Guid ticketId, long eventId, string remark, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> EventBelongsToTicketAsync(Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> InsertFakeBatchAsync(int count, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(string? search, Guid excludeTicketId, IReadOnlyCollection<Guid>? accessibleQueueIds, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<long>> GetMergedSourceTicketNumbersAsync(Guid targetTicketId, CancellationToken ct) => throw new NotImplementedException();
        public Task<MergeResult?> MergeAsync(Guid sourceTicketId, Guid targetTicketId, Guid actorUserId, bool acknowledgedCrossCustomer, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SplitChildTicket>> GetSplitChildrenAsync(Guid parentTicketId, CancellationToken ct) => throw new NotImplementedException();
        public Task<SplitResult?> SplitAsync(Guid sourceTicketId, long sourceMailEventId, string newSubject, Guid actorUserId, string? overrideBodyHtml, string? overrideBodyText, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class ThrowingDispatcher : ITriggerActionDispatcher
    {
        public Task<IReadOnlyList<TriggerActionResult>> DispatchAllAsync(JsonElement actions, TriggerEvaluationContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("DryRun must NEVER call the production action dispatcher.");
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromResult(default(T)!);
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }

    private sealed class ThrowingAudit : IAuditLogger
    {
        public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("DryRun must NEVER write audit events.");
    }

    private sealed class FakeRenderFactory : ITriggerRenderContextFactory
    {
        public Task<TriggerRenderContext> BuildAsync(TriggerEvaluationContext ctx, string? triggerLocale, string? triggerTimeZone, CancellationToken ct)
            => Task.FromResult(new TriggerRenderContext
            {
                StringValues = new Dictionary<string, string?>(StringComparer.Ordinal),
                DateTimeValues = new Dictionary<string, DateTime?>(StringComparer.Ordinal),
                DefaultTimeZoneId = null,
                Culture = CultureInfo.InvariantCulture,
            });
    }
}
