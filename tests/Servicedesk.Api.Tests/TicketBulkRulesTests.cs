using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Bulk = single (CLAUDE.md → Bulk actions): the real
/// <see cref="TicketBulkActionService"/> on top of the real
/// <see cref="TicketMutationService"/> and <see cref="TicketFieldRules"/>.
/// <c>TicketBulkActionServiceTests</c> fakes the mutation service; these
/// prove each leg is judged by the single-ticket precheck, that a refused
/// ticket is skipped with its reason and never written, and that the other
/// tickets of the same request still go through.
public sealed class TicketBulkRulesTests
{
    private static readonly Guid QueueA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid HiddenQueue = Guid.Parse("a0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Open = Guid.Parse("50000000-0000-0000-0000-00000000000a");
    private static readonly Guid Closed = Guid.Parse("50000000-0000-0000-0000-00000000000b");
    private static readonly Guid Agent = Guid.Parse("d0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Customer = Guid.Parse("d0000000-0000-0000-0000-00000000000b");
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TicketMutationActor Actor = new(
        Agent, "Agent", "agent@example.test", "Agent", "198.51.100.7", "tests");

    [Fact]
    public async Task Each_ticket_is_judged_by_the_single_ticket_rules_and_only_passing_ones_are_written()
    {
        var w = new World();
        var ok = w.Add(QueueA);
        var blocked = w.Add(QueueA, checklistBlocks: true);
        var hidden = w.Add(HiddenQueue);

        var result = await w.Bulk.ExecuteAsync(Actor, Request([ok, blocked, hidden], statusId: Closed), default);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(TicketBulkSkipReason.ChecklistIncomplete, Reason(result, blocked));
        Assert.Equal(TicketBulkSkipReason.NoAccess, Reason(result, hidden));
        Assert.Equal([ok], w.Written);
    }

    [Fact]
    public async Task A_status_change_that_needs_a_gate_confirmation_is_skipped_not_forced()
    {
        var w = new World { GateMatches = true };
        var ticket = w.Add(QueueA);

        var result = await w.Bulk.ExecuteAsync(Actor, Request([ticket], statusId: Closed), default);

        Assert.Equal(TicketBulkSkipReason.GateRequired, Reason(result, ticket));
        Assert.Empty(w.Written);
    }

    [Fact]
    public async Task An_assignee_that_is_not_an_active_agent_is_refused_on_every_ticket()
    {
        var w = new World();
        var first = w.Add(QueueA);
        var second = w.Add(QueueA);

        var result = await w.Bulk.ExecuteAsync(Actor, Request([first, second], assignee: Customer), default);

        Assert.Equal(0, result.Succeeded);
        Assert.All(result.Skipped, s => Assert.Equal(TicketBulkSkipReason.AssigneeNotAssignable, s.Reason));
        Assert.Empty(w.Written);
    }

    [Fact]
    public async Task A_status_outside_the_ticket_queue_scope_is_skipped()
    {
        var w = new World { StatusAllowed = (_, status) => status != Closed };
        var ticket = w.Add(QueueA);

        var result = await w.Bulk.ExecuteAsync(Actor, Request([ticket], statusId: Closed), default);

        Assert.Equal(TicketBulkSkipReason.StatusNotInQueueScope, Reason(result, ticket));
        Assert.Empty(w.Written);
    }

    [Fact]
    public async Task A_close_block_found_by_the_writer_is_a_skip_with_its_reason_not_a_failure()
    {
        // The precheck saw no blocker (e.g. a queue move whose auto-flip
        // lands on a closing status); the writer's re-check under the row
        // lock refuses it.
        var w = new World();
        var ticket = w.Add(QueueA, blockedAtWrite: true);
        var other = w.Add(QueueA);

        var result = await w.Bulk.ExecuteAsync(Actor, Request([ticket, other], statusId: Closed), default);

        Assert.Equal(TicketBulkSkipReason.ChecklistIncomplete, Reason(result, ticket));
        Assert.Equal(1, result.Succeeded);
    }

    [Fact]
    public async Task An_unknown_target_found_by_the_writer_is_a_skip_with_its_reason()
    {
        // E.g. a priority deleted after the agent opened the dialog: the
        // writer rolls back, the bulk reports it instead of "failed".
        var w = new World();
        var ticket = w.Add(QueueA, unknownAtWrite: true);
        var other = w.Add(QueueA);

        var result = await w.Bulk.ExecuteAsync(Actor, Request([ticket, other], statusId: Closed), default);

        Assert.Equal(TicketBulkSkipReason.UnknownTarget, Reason(result, ticket));
        Assert.Equal([other], w.Written);
    }

    // ---- plumbing -----------------------------------------------------------

    private static TicketBulkActionRequest Request(Guid[] ids, Guid? statusId = null, Guid? assignee = null) =>
        new(ids, MessageHtml: null, MessageIsInternal: true, StatusId: statusId, QueueId: null,
            PriorityId: null, AssigneeUserId: assignee, UnassignAssignee: false);

    private static string? Reason(TicketBulkActionResult result, Guid ticketId) =>
        result.Skipped.SingleOrDefault(s => s.TicketId == ticketId)?.Reason;

    /// Tickets in memory, the actor sees QueueA only; every collaborator
    /// outside the rules is a recording fake.
    private sealed class World
    {
        private readonly Dictionary<Guid, Ticket> _tickets = new();
        private readonly HashSet<Guid> _checklistBlocked = new();
        private readonly HashSet<Guid> _blockedAtWrite = new();
        private readonly HashSet<Guid> _unknownAtWrite = new();

        public bool GateMatches { get; init; }
        public Func<Guid, Guid, bool> StatusAllowed { get; init; } = (_, _) => true;
        public List<Guid> Written { get; } = new();

        public Guid Add(Guid queueId, bool checklistBlocks = false, bool blockedAtWrite = false, bool unknownAtWrite = false)
        {
            var id = Guid.NewGuid();
            _tickets[id] = new Ticket(
                Id: id, Number: 2000 + _tickets.Count, Subject: "Printer offline", RequesterContactId: Guid.NewGuid(),
                AssigneeUserId: null, QueueId: queueId, StatusId: Open, PriorityId: Guid.NewGuid(), CategoryId: null,
                Source: "Web", ExternalRef: null, CreatedUtc: Now.AddDays(-1), UpdatedUtc: Now.AddDays(-1), DueUtc: null,
                FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null, IsDeleted: false);
            if (checklistBlocks) _checklistBlocked.Add(id);
            if (blockedAtWrite) _blockedAtWrite.Add(id);
            if (unknownAtWrite) _unknownAtWrite.Add(id);
            return id;
        }

        private TicketBulkActionService? _bulk;
        public TicketBulkActionService Bulk => _bulk ??= Build();

        private TicketBulkActionService Build()
        {
            var tickets = Fake<ITicketRepository>.Create((m, a) => m.Name switch
            {
                nameof(ITicketRepository.GetCoreAsync) => Task.FromResult(_tickets.GetValueOrDefault((Guid)a![0]!)),
                nameof(ITicketRepository.UpdateFieldsAsync) => Write((Guid)a![0]!),
                _ => throw new InvalidOperationException("Unexpected repository call: " + m.Name),
            });
            var scope = Fake<IQueueStatusScope>.Create((m, a) => m.Name == nameof(IQueueStatusScope.IsStatusAllowedAsync)
                ? Task.FromResult(StatusAllowed((Guid)a![0]!, (Guid)a[1]!))
                : throw new InvalidOperationException("Unexpected scope call: " + m.Name));
            var guard = Fake<IChecklistCloseGuard>.Create((_, a) => Task.FromResult<IReadOnlyList<ChecklistBlocker>>(
                _checklistBlocked.Contains((Guid)a![0]!)
                    ? [new ChecklistBlocker(Guid.NewGuid(), "Onboarding", 2)]
                    : Array.Empty<ChecklistBlocker>()));
            var users = Fake<IUserService>.Create((_, a) => Task.FromResult<ApplicationUser?>(
                new ApplicationUser((Guid)a![0]!, "person@example.test", null, (Guid)a[0]! == Agent ? "Agent" : "Customer",
                    Now.AddYears(-1), null, 0, null, "Local", null, null, IsActive: true)));
            var gates = Fake<IStatusGateService>.Create((_, _) => Task.FromResult<IReadOnlyList<MatchedStatusGate>>(
                GateMatches ? [(MatchedStatusGate)null!] : Array.Empty<MatchedStatusGate>()));
            var settings = new InMemorySettingsService();

            var mutations = new TicketMutationService(
                tickets, new FakeQueueAccess(QueueA),
                new TicketFieldRules(scope, guard, settings, new ManualClock(Now), users),
                gates, new FakeAuditLogger(),
                Fake<ITicketListNotifier>.Create((_, _) => Task.CompletedTask),
                Fake<ISlaEngine>.Create((_, _) => Task.CompletedTask),
                Fake<ITriggerService>.Create((_, _) => Task.CompletedTask));
            return new TicketBulkActionService(
                mutations, settings, new FakeAuditLogger(), NullLogger<TicketBulkActionService>.Instance, new ManualClock(Now));
        }

        private Task<TicketDetail?> Write(Guid id)
        {
            if (_blockedAtWrite.Contains(id))
                throw new ChecklistBlockedException([new ChecklistBlocker(Guid.NewGuid(), "Onboarding", 1)]);
            if (_unknownAtWrite.Contains(id))
                throw new TicketFieldTargetNotFoundException("priority");
            Written.Add(id);
            var t = _tickets[id];
            return Task.FromResult<TicketDetail?>(new TicketDetail(
                t, new TicketBody(t.Id, "body", null), Array.Empty<TicketEvent>(), Array.Empty<TicketEventPin>()));
        }
    }
}
