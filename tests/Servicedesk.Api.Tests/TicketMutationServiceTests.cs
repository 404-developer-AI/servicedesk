using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The real <see cref="TicketMutationService"/> — the one rule set that the
/// single-ticket PATCH/events endpoints, bulk actions and trigger "Set status"
/// share. Every other test fakes it; these pin the rules themselves: the
/// precheck verdicts and their order, and the post-write fan-out (audit →
/// realtime → SLA → triggers, under the agent's trigger origin).
public sealed class TicketMutationServiceTests
{
    private static readonly Guid TicketId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid QueueA = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid QueueB = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid Open = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid Closed = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid Assignee = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TicketMutationActor Actor = new(
        Guid.Parse("c0000000-0000-0000-0000-000000000001"), "Agent",
        "agent@example.test", "Agent", "198.51.100.7", "tests");

    // ---- precheck: access ---------------------------------------------------

    [Fact]
    public async Task A_missing_ticket_is_not_found_and_nothing_else_is_asked()
    {
        var h = new Harness { Ticket = null };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(StatusId: Closed), default);

        Assert.Equal(TicketMutationCheck.NotFound, result.Check);
        Assert.Null(result.Ticket);
        Assert.Equal(new[] { "tickets.GetCoreAsync" }, h.Calls);
    }

    [Fact]
    public async Task No_access_to_the_current_queue_is_refused_before_any_other_rule()
    {
        var h = new Harness { Access = new FakeQueueAccess(QueueB) };

        // Every later rule would fail too (past pending-till, status out of scope).
        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(StatusId: Closed, PendingTillUtc: Now.AddDays(-1)), default);

        Assert.Equal(TicketMutationCheck.NoAccess, result.Check);
        Assert.Null(result.Ticket);
        Assert.Equal(new[] { "tickets.GetCoreAsync" }, h.Calls);
    }

    [Fact]
    public async Task Access_precheck_returns_the_ticket_for_a_queue_the_actor_can_see()
    {
        var h = new Harness();

        var result = await h.Service.PrecheckAccessAsync(Actor, TicketId, default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
        Assert.Equal(TicketId, result.Ticket!.Id);
    }

    // ---- precheck: pending-till ---------------------------------------------

    [Fact]
    public async Task A_pending_till_in_the_past_is_refused_on_server_time()
    {
        var h = new Harness();

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: Now.AddMinutes(-1)), default);

        Assert.Equal(TicketMutationCheck.PendingTillInPast, result.Check);
    }

    [Fact]
    public async Task Re_sending_the_pending_till_the_ticket_already_holds_passes_even_when_it_has_passed()
    {
        var past = Now.AddHours(-3);
        var h = new Harness { Ticket = Ticket() with { PendingTillUtc = past } };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: past, Subject: "unrelated edit"), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
    }

    [Fact]
    public async Task A_pending_till_in_the_future_passes()
    {
        var h = new Harness();

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: Now.AddDays(1)), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
    }

    [Fact]
    public async Task A_new_pending_till_on_a_ticket_that_stays_non_pending_is_refused()
    {
        var h = new Harness { IsPending = _ => false };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: Now.AddDays(1)), default);

        Assert.Equal(TicketMutationCheck.PendingTillNotPending, result.Check);
    }

    [Fact]
    public async Task Re_sending_the_held_pending_till_on_a_non_pending_ticket_passes()
    {
        var held = Now.AddDays(1);
        var h = new Harness { Ticket = Ticket() with { PendingTillUtc = held }, IsPending = _ => false };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(PendingTillUtc: held, Subject: "unrelated edit"), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
    }

    [Fact]
    public async Task A_pending_till_with_a_status_change_is_left_to_the_writer()
    {
        // The writer writes it on a move into Pending and drops it on a move
        // out; the rules do not judge the current status then.
        var h = new Harness { IsPending = _ => false };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(StatusId: Closed, PendingTillUtc: Now.AddDays(1)), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
        Assert.DoesNotContain("scope." + nameof(IQueueStatusScope.IsPendingStatusAsync), h.Calls);
    }

    // ---- precheck: assignee -------------------------------------------------

    [Theory]
    [InlineData("Customer", true)]
    [InlineData("Agent", false)]
    public async Task A_new_assignee_must_be_an_active_agent_or_admin(string role, bool active)
    {
        var h = new Harness { Users = { [Assignee] = User(Assignee, role, active) } };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(AssigneeUserId: Assignee), default);

        Assert.Equal(TicketMutationCheck.AssigneeNotAssignable, result.Check);
    }

    [Fact]
    public async Task An_unknown_assignee_is_refused_like_a_non_staff_one()
    {
        var h = new Harness();

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(AssigneeUserId: Assignee), default);

        Assert.Equal(TicketMutationCheck.AssigneeNotAssignable, result.Check);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Admin")]
    public async Task An_active_agent_or_admin_can_be_assigned(string role)
    {
        var h = new Harness { Users = { [Assignee] = User(Assignee, role, active: true) } };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(AssigneeUserId: Assignee), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
    }

    [Fact]
    public async Task Keeping_a_deactivated_assignee_does_not_block_an_unrelated_edit()
    {
        var h = new Harness
        {
            Ticket = Ticket() with { AssigneeUserId = Assignee },
            Users = { [Assignee] = User(Assignee, "Agent", active: false) },
        };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(AssigneeUserId: Assignee, Subject: "unrelated edit"), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
        Assert.DoesNotContain("users.FindByIdAsync", h.Calls);
    }

    // ---- precheck: queue moves ----------------------------------------------

    [Fact]
    public async Task Moving_to_a_queue_without_access_is_refused()
    {
        var h = new Harness { Access = new FakeQueueAccess(QueueA) };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(QueueId: QueueB), default);

        Assert.Equal(TicketMutationCheck.TargetQueueNoAccess, result.Check);
    }

    [Fact]
    public async Task Re_sending_the_current_queue_is_not_a_move()
    {
        var h = new Harness { Access = new FakeQueueAccess(QueueA), Ticket = Ticket() with { IsProject = true } };
        h.Settings.With(SettingKeys.Projects.QueueId, QueueB.ToString());

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(QueueId: QueueA), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
    }

    [Fact]
    public async Task A_project_ticket_cannot_leave_the_pinned_project_queue()
    {
        var pinned = QueueA;
        var h = new Harness { Ticket = Ticket() with { IsProject = true } };
        h.Settings.With(SettingKeys.Projects.QueueId, pinned.ToString());

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(QueueId: QueueB), default);

        Assert.Equal(TicketMutationCheck.ProjectQueueLocked, result.Check);
    }

    [Fact]
    public async Task A_project_ticket_moves_freely_when_no_project_queue_is_pinned()
    {
        var h = new Harness { Ticket = Ticket() with { IsProject = true } };
        h.Settings.With(SettingKeys.Projects.QueueId, "");

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(QueueId: QueueB), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
    }

    [Fact]
    public async Task A_normal_ticket_ignores_the_project_queue_pin()
    {
        var h = new Harness();
        h.Settings.With(SettingKeys.Projects.QueueId, QueueA.ToString());

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(QueueId: QueueB), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
    }

    // ---- precheck: status ---------------------------------------------------

    [Fact]
    public async Task An_explicit_status_must_be_allowed_in_the_target_queue()
    {
        var h = new Harness { StatusAllowed = (queue, _) => queue == QueueA };

        var stay = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(StatusId: Closed), default);
        var move = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId,
            new TicketFieldUpdate(QueueId: QueueB, StatusId: Closed), default);

        Assert.Equal(TicketMutationCheck.Ok, stay.Check);
        Assert.Equal(TicketMutationCheck.StatusNotInQueueScope, move.Check);
    }

    [Fact]
    public async Task A_queue_only_move_leaves_the_status_to_the_repository_auto_flip()
    {
        var h = new Harness { StatusAllowed = (_, _) => false };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(QueueId: QueueB), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
        Assert.DoesNotContain("scope.IsStatusAllowedAsync", h.Calls);
    }

    [Fact]
    public async Task Open_required_checklist_items_block_a_status_change_with_the_blockers_named()
    {
        var blocker = new ChecklistBlocker(Guid.NewGuid(), "Onboarding", 2);
        var h = new Harness { Blockers = new[] { blocker } };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(StatusId: Closed), default);

        Assert.Equal(TicketMutationCheck.ChecklistIncomplete, result.Check);
        Assert.Equal(new[] { blocker }, result.ChecklistBlockers);
        Assert.DoesNotContain("gates.FindMatchingAsync", h.Calls); // a hard block, never a gate
    }

    [Fact]
    public async Task Re_sending_the_current_status_asks_neither_the_checklist_guard_nor_the_gates()
    {
        var h = new Harness { Blockers = new[] { new ChecklistBlocker(Guid.NewGuid(), "x", 1) } };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(StatusId: Open), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
        Assert.Empty(result.Gates);
        Assert.DoesNotContain("guard.FindBlockersAsync", h.Calls);
        Assert.DoesNotContain("gates.FindMatchingAsync", h.Calls);
    }

    [Fact]
    public async Task A_status_change_returns_the_matching_gates_with_the_ticket()
    {
        var h = new Harness { GateCount = 1 };

        var result = await h.Service.PrecheckFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(StatusId: Closed), default);

        Assert.Equal(TicketMutationCheck.Ok, result.Check);
        Assert.Equal(TicketId, result.Ticket!.Id);
        Assert.Contains("gates.FindMatchingAsync", h.Calls);
        Assert.Same(h.GateResult, result.Gates);
    }

    // ---- publish ------------------------------------------------------------

    [Fact]
    public async Task Publishing_a_field_update_audits_then_notifies_recalcs_sla_and_runs_triggers_as_the_agent()
    {
        var h = new Harness();
        var update = new TicketFieldUpdate(PriorityId: Guid.NewGuid(), ClearAssignee: true);

        await h.Service.PublishFieldUpdateAsync(Actor, TicketId, update, new { priority = "high" }, changeSet: null, default);

        Assert.Equal(new[] { "notifier.NotifyUpdatedAsync", "sla.OnTicketFieldsChangedAsync", "triggers.EvaluateAsync" }, h.Calls);
        var audit = Assert.Single(h.Audit.Events);
        Assert.Equal("ticket.updated", audit.EventType);
        Assert.Equal(Actor.AuditActor, audit.Actor);
        Assert.Equal(Actor.AuditRole, audit.ActorRole);
        Assert.Equal(TicketId.ToString(), audit.Target);
        Assert.Equal(Actor.ClientIp, audit.ClientIp);

        Assert.Equal(TriggerActivatorKind.Action, h.TriggerActivator);
        Assert.Null(h.TriggerEventId);
        Assert.False(h.TriggerChangeSet!.ArticleAdded);
        Assert.Equal(
            new[] { TriggerFieldKeys.TicketOwnerId, TriggerFieldKeys.TicketPriorityId }.OrderBy(x => x),
            h.TriggerChangeSet.ChangedFields.OrderBy(x => x));
        Assert.Equal(Actor.UserId, h.TriggerOriginDuringEvaluate);
        Assert.Null(TriggerOrigin.OnBehalfOfUserId); // the scope is closed again
    }

    [Fact]
    public async Task A_caller_supplied_change_set_is_passed_to_the_triggers_unchanged()
    {
        var h = new Harness();
        var own = new TriggerChangeSet(new HashSet<string> { TriggerFieldKeys.TicketSubject }, ArticleAdded: false);

        await h.Service.PublishFieldUpdateAsync(Actor, TicketId, new TicketFieldUpdate(PriorityId: Guid.NewGuid()), new { }, own, default);

        Assert.Same(own, h.TriggerChangeSet);
    }

    [Fact]
    public async Task Publishing_an_event_audits_and_runs_article_triggers_for_that_event()
    {
        var h = new Harness();
        var evt = Event(id: 42, type: "Note");

        await h.Service.PublishEventAsync(Actor, TicketId, evt, new { kind = "note" }, default);

        Assert.Equal(new[] { "notifier.NotifyUpdatedAsync", "sla.OnTicketEventAsync:Note", "triggers.EvaluateAsync" }, h.Calls);
        Assert.Equal("ticket.event.added", Assert.Single(h.Audit.Events).EventType);
        Assert.Equal(42, h.TriggerEventId);
        Assert.True(h.TriggerChangeSet!.ArticleAdded);
        Assert.Empty(h.TriggerChangeSet.ChangedFields);
        Assert.Equal(Actor.UserId, h.TriggerOriginDuringEvaluate);
    }

    // ---- add event ----------------------------------------------------------

    [Fact]
    public async Task An_html_only_event_gets_a_plain_text_body_for_search_and_triggers()
    {
        var h = new Harness();

        await h.Service.AddEventAsync(TicketId,
            new NewTicketEvent("Note", BodyText: null, BodyHtml: "<p>Printer <b>offline</b></p>", IsInternal: true, AuthorUserId: Actor.UserId),
            default);

        Assert.Equal("Printer offline", h.AddedEvent!.BodyText?.Trim());
        Assert.Equal("<p>Printer <b>offline</b></p>", h.AddedEvent.BodyHtml);
    }

    [Fact]
    public async Task An_explicit_plain_text_body_is_kept()
    {
        var h = new Harness();

        await h.Service.AddEventAsync(TicketId,
            new NewTicketEvent("Note", BodyText: "typed text", BodyHtml: "<p>other</p>", IsInternal: true, AuthorUserId: Actor.UserId),
            default);

        Assert.Equal("typed text", h.AddedEvent!.BodyText);
    }

    [Fact]
    public void Clear_flags_count_as_changed_fields()
    {
        var changed = TicketMutationService.DeriveChangedFields(new TicketFieldUpdate(ClearAssignee: true, ClearCategory: true));

        Assert.Equal(
            new[] { TriggerFieldKeys.TicketCategoryId, TriggerFieldKeys.TicketOwnerId }.OrderBy(x => x),
            changed.OrderBy(x => x));
    }

    [Fact]
    public void An_empty_update_changes_nothing()
    {
        Assert.Empty(TicketMutationService.DeriveChangedFields(new TicketFieldUpdate()));
    }

    // ---- plumbing -----------------------------------------------------------

    private static Ticket Ticket() => new(
        Id: TicketId, Number: 1001, Subject: "Printer offline", RequesterContactId: Guid.NewGuid(),
        AssigneeUserId: null, QueueId: QueueA, StatusId: Open, PriorityId: Guid.NewGuid(), CategoryId: null,
        Source: "Web", ExternalRef: null, CreatedUtc: Now.AddDays(-1), UpdatedUtc: Now.AddDays(-1), DueUtc: null,
        FirstResponseUtc: null, ResolvedUtc: null, ClosedUtc: null, IsDeleted: false);

    private static ApplicationUser User(Guid id, string role, bool active) => new(
        id, "person@example.test", null, role, Now.AddYears(-1), null, 0, null, "Local", null, null, active);

    private static TicketEvent Event(long id, string type) => new(
        Id: id, TicketId: TicketId, EventType: type, AuthorUserId: Actor.UserId, AuthorContactId: null,
        AuthorName: Actor.AuditActor, BodyText: "x", BodyHtml: "<p>x</p>", MetadataJson: "{}",
        IsInternal: true, CreatedUtc: Now, EditedUtc: null, EditedByUserId: null);

    /// Wires the real service to recording fakes. Defaults: the ticket sits in
    /// QueueA with status Open, the actor sees every queue, every status is
    /// allowed, no checklist blocks and no gate matches.
    private sealed class Harness
    {
        public Ticket? Ticket { get; init; } = TicketMutationServiceTests.Ticket();
        public FakeQueueAccess Access { get; init; } = FakeQueueAccess.All();
        public Func<Guid, Guid, bool> StatusAllowed { get; init; } = (_, _) => true;
        /// Whether a status is Pending-category; by default every status is.
        public Func<Guid, bool> IsPending { get; init; } = _ => true;
        public IReadOnlyList<ChecklistBlocker> Blockers { get; init; } = Array.Empty<ChecklistBlocker>();
        /// The gates the probe "matches" — content is irrelevant to the
        /// service, it passes the list through.
        public int GateCount { get; init; }
        public IReadOnlyList<MatchedStatusGate> GateResult { get; private set; } = Array.Empty<MatchedStatusGate>();

        /// Users the assignee rule can find; any other id is unknown.
        public Dictionary<Guid, ApplicationUser> Users { get; } = new();
        public InMemorySettingsService Settings { get; } = new();
        public FakeAuditLogger Audit { get; } = new();
        public List<string> Calls { get; } = new();

        public NewTicketEvent? AddedEvent { get; private set; }
        public TriggerActivatorKind? TriggerActivator { get; private set; }
        public long? TriggerEventId { get; private set; }
        public TriggerChangeSet? TriggerChangeSet { get; private set; }
        public Guid? TriggerOriginDuringEvaluate { get; private set; }

        private TicketMutationService? _service;
        public TicketMutationService Service => _service ??= Build();

        private TicketMutationService Build()
        {
            GateResult = GateCount == 0
                ? Array.Empty<MatchedStatusGate>()
                : Enumerable.Range(0, GateCount).Select(_ => (MatchedStatusGate)null!).ToArray();

            var tickets = Fake<ITicketRepository>.Create((m, a) =>
            {
                Calls.Add("tickets." + m.Name);
                return m.Name switch
                {
                    nameof(ITicketRepository.GetCoreAsync) => Task.FromResult(Ticket),
                    nameof(ITicketRepository.AddEventAsync) => Record((NewTicketEvent)a![1]!),
                    _ => throw new InvalidOperationException("Unexpected repository call: " + m.Name),
                };
            });
            var scope = Fake<IQueueStatusScope>.Create((m, a) =>
            {
                Calls.Add("scope." + m.Name);
                return m.Name == nameof(IQueueStatusScope.IsPendingStatusAsync)
                    ? Task.FromResult(IsPending((Guid)a![0]!))
                    : Task.FromResult(StatusAllowed((Guid)a![0]!, (Guid)a[1]!));
            });
            var gates = Fake<IStatusGateService>.Create((m, _) =>
            {
                Calls.Add("gates." + m.Name);
                return Task.FromResult(GateResult);
            });
            var guard = Fake<IChecklistCloseGuard>.Create((m, _) =>
            {
                Calls.Add("guard." + m.Name);
                return Task.FromResult(Blockers);
            });
            var notifier = Fake<ITicketListNotifier>.Create((m, _) => { Calls.Add("notifier." + m.Name); return Task.CompletedTask; });
            var sla = Fake<ISlaEngine>.Create((m, a) =>
            {
                Calls.Add(m.Name == nameof(ISlaEngine.OnTicketEventAsync) ? $"sla.{m.Name}:{a![1]}" : "sla." + m.Name);
                return Task.CompletedTask;
            });
            var triggers = Fake<ITriggerService>.Create((m, a) =>
            {
                Calls.Add("triggers." + m.Name);
                if (m.Name != nameof(ITriggerService.EvaluateAsync)) throw new InvalidOperationException(m.Name);
                TriggerEventId = (long?)a![1];
                TriggerActivator = (TriggerActivatorKind)a[2]!;
                TriggerChangeSet = (TriggerChangeSet)a[3]!;
                TriggerOriginDuringEvaluate = TriggerOrigin.OnBehalfOfUserId;
                return Task.CompletedTask;
            });

            var users = Fake<IUserService>.Create((m, a) =>
            {
                Calls.Add("users." + m.Name);
                if (m.Name != nameof(IUserService.FindByIdAsync)) throw new InvalidOperationException(m.Name);
                return Task.FromResult(Users.GetValueOrDefault((Guid)a![0]!));
            });

            return new TicketMutationService(tickets, Access, new TicketFieldRules(scope, guard, Settings, new FixedClock(Now), users),
                gates, Audit, notifier, sla, triggers);
        }

        private Task<TicketEvent?> Record(NewTicketEvent input)
        {
            AddedEvent = input;
            return Task.FromResult<TicketEvent?>(Event(1, input.EventType));
        }
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
