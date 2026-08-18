using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.Actions;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.103 — a trigger's set-status action honours the checklist close
/// block: with an incomplete blocking checklist the action is a no-op
/// (reason checklist_incomplete), the reporter is told (timeline event +
/// notification for the causing agent) and the mutator is never reached.
/// Same-status and unblocked calls never consult the reporter.
public sealed class SetStatusHandlerChecklistBlockTests
{
    private static readonly Guid TriggerId = Guid.NewGuid();
    private static readonly Guid CurrentStatus = Guid.NewGuid();
    private static readonly Guid ClosedStatus = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();

    [Fact]
    public async Task Blocked_status_change_is_a_noop_and_reports_to_the_causing_agent()
    {
        var guard = new FakeGuard(blockers: 1);
        var reporter = new RecordingReporter();
        // Mutator with a null data source: if the handler ever reached it,
        // the test would blow up with a NullReferenceException.
        var handler = new SetStatusHandler(new SystemFieldMutator(null!, null!), guard, reporter, new FakeTriggers("Closed without Invoice [CWI]"));

        var result = await handler.ApplyAsync(Action(ClosedStatus), Context(triggeringAuthor: AgentId), default);

        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        var summary = JsonSerializer.Serialize(result.ChangeSummary);
        Assert.Contains("checklist_incomplete", summary);
        Assert.Contains("Onboarding", summary);

        var call = Assert.Single(reporter.Calls);
        Assert.Equal("Closed without Invoice [CWI]", call.TriggerName);
        Assert.Equal(ClosedStatus, call.TargetStatusId);
        Assert.Equal(AgentId, call.TriggeringEvent?.AuthorUserId);
        Assert.Single(call.Blockers);
    }

    [Fact]
    public async Task Same_status_is_a_noop_without_consulting_guard_or_reporter()
    {
        var guard = new FakeGuard(blockers: 1);
        var reporter = new RecordingReporter();
        var handler = new SetStatusHandler(new SystemFieldMutator(null!, null!), guard, reporter, new FakeTriggers("x"));

        // Same-status: the handler skips the guard and the mutator answers
        // no-op from the ids alone (no DB round-trip).
        var result = await handler.ApplyAsync(Action(CurrentStatus), Context(null), default);
        Assert.Equal(TriggerActionStatus.NoOp, result.Status);
        Assert.Equal(0, guard.Calls);
        Assert.Empty(reporter.Calls);
    }

    [Fact]
    public async Task Unblocked_change_falls_through_to_the_mutator_without_reporting()
    {
        var guard = new FakeGuard(blockers: 0);
        var reporter = new RecordingReporter();
        var handler = new SetStatusHandler(new SystemFieldMutator(null!, null!), guard, reporter, new FakeTriggers("x"));

        // Reaching the (null-backed) mutator proves the guard let it through.
        await Assert.ThrowsAnyAsync<Exception>(() => handler.ApplyAsync(Action(ClosedStatus), Context(null), default));
        Assert.Equal(1, guard.Calls);
        Assert.Empty(reporter.Calls);
    }

    // ---- helpers ----

    private static JsonElement Action(Guid statusId)
        => JsonDocument.Parse(JsonSerializer.Serialize(new { kind = "set_status", status_id = statusId })).RootElement;

    private static TriggerEvaluationContext Context(Guid? triggeringAuthor)
    {
        var ticketId = Guid.NewGuid();
        var ticket = new Ticket(
            ticketId, 42, "Onboarding ACME", Guid.NewGuid(), null, Guid.NewGuid(), CurrentStatus, Guid.NewGuid(), null,
            "web", null, DateTime.UtcNow, DateTime.UtcNow, null, null, null, null, false);
        TicketEvent? evt = triggeringAuthor is null
            ? null
            : new TicketEvent(7, ticketId, "Note", triggeringAuthor, null, "agent@x", "Closed without Invoice [CWI]", null, "{}", true, DateTime.UtcNow, null, null);
        return new TriggerEvaluationContext(ticketId, ticket, evt, TriggerChangeSet.ArticleOnly(isTicketCreation: false), DateTime.UtcNow, TriggerId);
    }

    private sealed class FakeGuard : IChecklistCloseGuard
    {
        private readonly int _blockers;
        public int Calls;
        public FakeGuard(int blockers) => _blockers = blockers;
        public Task<IReadOnlyList<ChecklistBlocker>> FindBlockersAsync(Guid ticketId, Guid targetStatusId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ChecklistBlocker>>(
                Enumerable.Range(0, _blockers).Select(_ => new ChecklistBlocker(Guid.NewGuid(), "Onboarding", 5)).ToList());
        }
    }

    private sealed class RecordingReporter : IChecklistCloseBlockReporter
    {
        public readonly List<(Ticket Ticket, TicketEvent? TriggeringEvent, Guid TriggerId, string TriggerName, Guid TargetStatusId, IReadOnlyList<ChecklistBlocker> Blockers)> Calls = new();
        public Task ReportTriggerBlockedAsync(Ticket ticket, TicketEvent? triggeringEvent, Guid triggerId, string triggerName, Guid targetStatusId, IReadOnlyList<ChecklistBlocker> blockers, CancellationToken ct)
        {
            Calls.Add((ticket, triggeringEvent, triggerId, triggerName, targetStatusId, blockers));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTriggers : ITriggerRepository
    {
        private readonly string _name;
        public FakeTriggers(string name) => _name = name;
        public Task<TriggerRow?> GetByIdAsync(Guid triggerId, CancellationToken ct)
            => Task.FromResult<TriggerRow?>(new TriggerRow { Id = triggerId, Name = _name });

        public Task<IReadOnlyList<TriggerRow>> LoadActiveAsync(TriggerActivatorKind activatorKind, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRow>> ListAllAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow> CreateAsync(NewTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<TriggerRow?> UpdateAsync(Guid id, UpdateTrigger row, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task ReorderAsync(IReadOnlyList<TriggerPlacement> placements, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TriggerRunSummary>> GetRunSummariesAsync(DateTime sinceUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerRunDetail>> ListRunsAsync(Guid triggerId, int limit, DateTime? cursorUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task RecordRunAsync(TriggerRunRecord record, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListReminderCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationCandidatesAsync(int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TriggerScheduleCandidate>> ListEscalationWarningCandidatesAsync(int warningMinutes, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> DeleteSkippedRunsOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct) => throw new NotImplementedException();
    }
}
