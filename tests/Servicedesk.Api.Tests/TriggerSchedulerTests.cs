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

/// Tests for v0.0.24 Blok 5 — TriggerService.EvaluateScheduledAsync.
/// The scheduler worker itself is a thin loop over candidate rows; the
/// behaviour worth testing in isolation is what happens to a single
/// (trigger, ticket, boundary) tuple after the scan.
public class TriggerSchedulerTests
{
    [Fact]
    public async Task Inactive_trigger_is_a_no_op()
    {
        // Race: trigger was deactivated between candidate scan and dispatch.
        // The evaluator must not write a trigger_run row (no boundary to
        // attach it to) and must not call the dispatcher.
        var trigger = MakeTrigger(isActive: false);
        var repo = new FakeTriggerRepo(trigger);
        var dispatcher = new FakeDispatcher();
        var matcher = new FakeMatcher(matches: true);
        var service = BuildService(repo, dispatcher, matcher, new FakeTicketRepo());

        var outcome = await service.EvaluateScheduledAsync(
            trigger.Id, Guid.NewGuid(), DateTime.UtcNow, "reminder", CancellationToken.None);

        Assert.Null(outcome);
        Assert.Empty(repo.Recorded);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task Missing_ticket_is_a_no_op()
    {
        // Trigger fired, candidate scan said this ticket was eligible, but
        // by the time we read it the ticket was deleted. Skip silently —
        // the row would dangle without a ticket FK target anyway.
        var trigger = MakeTrigger();
        var repo = new FakeTriggerRepo(trigger);
        var tickets = new FakeTicketRepo(); // returns null
        var service = BuildService(repo, new FakeDispatcher(), new FakeMatcher(true), tickets);

        var outcome = await service.EvaluateScheduledAsync(
            trigger.Id, Guid.NewGuid(), DateTime.UtcNow, "reminder", CancellationToken.None);

        Assert.Null(outcome);
        Assert.Empty(repo.Recorded);
    }

    [Fact]
    public async Task Matcher_false_records_skipped_no_match()
    {
        // Time-trigger conditions narrow which tickets fire (e.g. "queue =
        // support"). When the matcher rejects the ticket we still record a
        // run row so the admin's "is this trigger ever firing" page
        // surfaces the no-match outcomes — but with no diff, since nothing
        // happened.
        var ticketId = Guid.NewGuid();
        var trigger = MakeTrigger();
        var repo = new FakeTriggerRepo(trigger);
        var tickets = new FakeTicketRepo(SeedTicket(ticketId));
        var dispatcher = new FakeDispatcher();
        var matcher = new FakeMatcher(matches: false);
        var service = BuildService(repo, dispatcher, matcher, tickets);

        var outcome = await service.EvaluateScheduledAsync(
            trigger.Id, ticketId, DateTime.UtcNow, "reminder", CancellationToken.None);

        Assert.Equal(TriggerRunOutcome.SkippedNoMatch, outcome);
        var record = Assert.Single(repo.Recorded);
        Assert.Equal(TriggerRunOutcome.SkippedNoMatch, record.Outcome);
        Assert.Null(record.AppliedChangesJson);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task Matcher_true_with_applied_action_records_applied_with_boundary_in_diff()
    {
        var ticketId = Guid.NewGuid();
        var trigger = MakeTrigger();
        var repo = new FakeTriggerRepo(trigger);
        var tickets = new FakeTicketRepo(SeedTicket(ticketId));
        var dispatcher = new FakeDispatcher(new[]
        {
            TriggerActionResult.Applied("set_priority", new { from = "low", to = "high" }),
        });
        var matcher = new FakeMatcher(matches: true);
        var service = BuildService(repo, dispatcher, matcher, tickets);

        var boundary = new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);
        var outcome = await service.EvaluateScheduledAsync(
            trigger.Id, ticketId, boundary, "reminder", CancellationToken.None);

        Assert.Equal(TriggerRunOutcome.Applied, outcome);
        var record = Assert.Single(repo.Recorded);
        Assert.Equal(TriggerRunOutcome.Applied, record.Outcome);
        Assert.NotNull(record.AppliedChangesJson);
        // The boundary moment is embedded in the diff so the run-history
        // page can show "fired because deadline T was crossed".
        Assert.Contains("boundary_utc", record.AppliedChangesJson!, StringComparison.Ordinal);
        Assert.Contains("2026-04-27", record.AppliedChangesJson, StringComparison.Ordinal);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task Dispatcher_failure_records_failed_outcome()
    {
        var ticketId = Guid.NewGuid();
        var trigger = MakeTrigger();
        var repo = new FakeTriggerRepo(trigger);
        var tickets = new FakeTicketRepo(SeedTicket(ticketId));
        var dispatcher = new FakeDispatcher(new[]
        {
            TriggerActionResult.Failed("send_mail", "SMTP timeout"),
        });
        var matcher = new FakeMatcher(matches: true);
        var service = BuildService(repo, dispatcher, matcher, tickets);

        var outcome = await service.EvaluateScheduledAsync(
            trigger.Id, ticketId, DateTime.UtcNow, "reminder", CancellationToken.None);

        Assert.Equal(TriggerRunOutcome.Failed, outcome);
        var record = Assert.Single(repo.Recorded);
        Assert.Equal(TriggerRunOutcome.Failed, record.Outcome);
    }

    [Fact]
    public async Task Activator_mismatch_records_failed_without_dispatch()
    {
        // The scheduler tells the evaluator what activator pair it
        // expected to dispatch (chained-pointer setups still see the
        // trigger via the JOIN even if the admin re-typed the trigger
        // afterwards). On mismatch we must NOT run the trigger's
        // actions — they were authored for a different activator path.
        var ticketId = Guid.NewGuid();
        // Trigger was retyped to action:selective in flight.
        var trigger = MakeTrigger();
        trigger.ActivatorKind = "action";
        trigger.ActivatorMode = "selective";
        var repo = new FakeTriggerRepo(trigger);
        var tickets = new FakeTicketRepo(SeedTicket(ticketId));
        var dispatcher = new FakeDispatcher();
        var matcher = new FakeMatcher(matches: true);
        var service = BuildService(repo, dispatcher, matcher, tickets);

        var outcome = await service.EvaluateScheduledAsync(
            trigger.Id, ticketId, DateTime.UtcNow, "reminder", CancellationToken.None);

        Assert.Equal(TriggerRunOutcome.Failed, outcome);
        var record = Assert.Single(repo.Recorded);
        Assert.Equal(TriggerRunOutcome.Failed, record.Outcome);
        Assert.Equal("ActivatorMismatch", record.ErrorClass);
        Assert.Equal(0, dispatcher.Calls);
    }

    // ----- fakes -----

    private static TriggerRow MakeTrigger(bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "010 - Test reminder",
        Description = string.Empty,
        IsActive = isActive,
        ActivatorKind = "time",
        ActivatorMode = "reminder",
        ConditionsJson = "{\"op\":\"AND\",\"items\":[]}",
        ActionsJson = "[]",
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

    private static TriggerService BuildService(
        FakeTriggerRepo repo, FakeDispatcher dispatcher, FakeMatcher matcher, FakeTicketRepo tickets)
    {
        return new TriggerService(
            repo,
            tickets,
            matcher,
            dispatcher,
            new FakePreviewDispatcher(),
            new TriggerLoopGuard(),
            new FakeSettings(),
            new FakeAudit(),
            new FakeRenderFactory(),
            new Servicedesk.Infrastructure.Realtime.NullTicketListNotifier(),
            NullLogger<TriggerService>.Instance);
    }

    private sealed class FakeTriggerRepo : ITriggerRepository
    {
        private readonly TriggerRow _row;
        public List<TriggerRunRecord> Recorded { get; } = new();
        public FakeTriggerRepo(TriggerRow row) => _row = row;
        public Task<IReadOnlyList<TriggerRow>> LoadActiveAsync(TriggerActivatorKind activatorKind, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerRow>>(new[] { _row });
        public Task<TriggerRow?> GetByIdAsync(Guid triggerId, CancellationToken ct)
            => Task.FromResult<TriggerRow?>(triggerId == _row.Id ? _row : null);
        public Task RecordRunAsync(TriggerRunRecord record, CancellationToken ct)
        {
            Recorded.Add(record);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListReminderCandidatesAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerScheduleCandidate>>(Array.Empty<TriggerScheduleCandidate>());
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationCandidatesAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerScheduleCandidate>>(Array.Empty<TriggerScheduleCandidate>());
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationWarningCandidatesAsync(int warningMinutes, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerScheduleCandidate>>(Array.Empty<TriggerScheduleCandidate>());
        public Task<IReadOnlyList<TriggerRow>> ListAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerRow>>(new[] { _row });
        public Task<TriggerRow> CreateAsync(NewTrigger row, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TriggerRow?> UpdateAsync(Guid id, UpdateTrigger row, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TriggerRunSummary>> GetRunSummariesAsync(DateTime sinceUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, TriggerRunSummary>>(new Dictionary<Guid, TriggerRunSummary>());
        public Task<IReadOnlyList<TriggerRunDetail>> ListRunsAsync(Guid triggerId, int limit, DateTime? cursorUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerRunDetail>>(Array.Empty<TriggerRunDetail>());
    }

    private sealed class FakeDispatcher : ITriggerActionDispatcher
    {
        private readonly IReadOnlyList<TriggerActionResult> _results;
        public int Calls { get; private set; }
        public FakeDispatcher() : this(Array.Empty<TriggerActionResult>()) { }
        public FakeDispatcher(IEnumerable<TriggerActionResult> results) { _results = results.ToList(); }
        public Task<IReadOnlyList<TriggerActionResult>> DispatchAllAsync(JsonElement actions, TriggerEvaluationContext ctx, CancellationToken ct)
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

    private sealed class FakeTicketRepo : ITicketRepository
    {
        private readonly TicketDetail? _detail;
        public FakeTicketRepo() => _detail = null;
        public FakeTicketRepo(TicketDetail detail) => _detail = detail;
        public Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_detail is not null && _detail.Ticket.Id == id ? _detail : null);

        // Unused — throw on access so an accidental call is loud.
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

    private sealed class FakeSettings : ISettingsService
    {
        public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            // Triggers.MaxChainPerMutation is the only setting EvaluateScheduledAsync reads.
            if (typeof(T) == typeof(int)) return Task.FromResult((T)(object)10);
            return Task.FromResult(default(T)!);
        }
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }

    private sealed class FakeAudit : IAuditLogger
    {
        public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePreviewDispatcher : ITriggerActionPreviewDispatcher
    {
        public Task<IReadOnlyList<TriggerActionPreviewResult>> PreviewAllAsync(
            JsonElement actions, TriggerEvaluationContext ctx, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerActionPreviewResult>>(Array.Empty<TriggerActionPreviewResult>());
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
