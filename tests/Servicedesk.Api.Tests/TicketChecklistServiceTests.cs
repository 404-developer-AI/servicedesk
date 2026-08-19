using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Tickets;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.103 — the rules of <see cref="TicketChecklistService"/> against
/// in-memory fakes: queue-scope on attach, caps, detach rights (untouched →
/// agent, touched → admin only), ad-hoc item rights, n/a needs a reason,
/// completion transitions write the timeline events, and per-item timeline
/// logging follows the setting.
public sealed class TicketChecklistServiceTests
{
    private static readonly Guid QueueA = Guid.NewGuid();
    private static readonly Guid QueueB = Guid.NewGuid();
    private static readonly Guid TicketInA = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly Guid OtherAgentId = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();

    private static readonly TicketMutationActor Agent = new(AgentId, "Agent", "agent@x", "Agent", null, null);
    private static readonly TicketMutationActor OtherAgent = new(OtherAgentId, "Agent", "other@x", "Agent", null, null);
    private static readonly TicketMutationActor Admin = new(AdminId, "Admin", "admin@x", "Admin", null, null);

    // ---- attach ---------------------------------------------------------

    [Fact]
    public async Task Attach_snapshots_the_template_and_logs_event_audit_and_realtime()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Onboarding", queueIds: new[] { QueueA }, items: 3);

        var view = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);

        Assert.Equal("Onboarding", view.Checklist.Name);
        Assert.Equal(3, view.Items.Count);
        Assert.Equal(3, view.Checklist.RequiredTotal);
        Assert.Contains(f.Tickets.Events, e => e.EventType == "ChecklistAttached");
        Assert.Contains(f.Audit.Rows, r => r.EventType == "ticket.checklist.attached");
        Assert.Contains(TicketInA, f.Notifier.Notified);
    }

    [Fact]
    public async Task Attach_refuses_a_template_outside_the_tickets_queue_scope()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Field only", queueIds: new[] { QueueB }, items: 2);

        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default));
        Assert.Equal(ChecklistRejectCode.TemplateNotAvailable, ex.Code);
        Assert.Empty(f.Repo.Checklists);
    }

    [Fact]
    public async Task Attach_refuses_an_inactive_template_but_accepts_all_queues_scope()
    {
        var f = new Fixture();
        var inactive = f.Templates.Add("Old", queueIds: Array.Empty<Guid>(), items: 1, active: false);
        var global = f.Templates.Add("Global", queueIds: Array.Empty<Guid>(), items: 1);

        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.AttachAsync(Agent, TicketInA, inactive.Id, default));
        Assert.Equal(ChecklistRejectCode.TemplateNotAvailable, ex.Code);

        var view = await f.Service.AttachAsync(Agent, TicketInA, global.Id, default);
        Assert.Equal("Global", view.Checklist.Name);
    }

    [Fact]
    public async Task Attach_refuses_when_disabled_or_over_the_cap()
    {
        var f = new Fixture(maxPerTicket: 1);
        var tpl = f.Templates.Add("Onboarding", queueIds: new[] { QueueA }, items: 1);
        await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);

        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default));
        Assert.Equal(ChecklistRejectCode.TooManyChecklists, ex.Code);

        f.Settings.Enabled = false;
        var ex2 = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default));
        Assert.Equal(ChecklistRejectCode.Disabled, ex2.Code);
    }

    [Fact]
    public async Task No_ticket_access_reads_as_not_found()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Onboarding", queueIds: Array.Empty<Guid>(), items: 1);
        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.AttachAsync(Agent, Guid.NewGuid(), tpl.Id, default));
        Assert.Equal(ChecklistRejectCode.NotFound, ex.Code);
        var ex2 = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.ListAsync(Agent, Guid.NewGuid(), default));
        Assert.Equal(ChecklistRejectCode.NotFound, ex2.Code);
    }

    // ---- detach ---------------------------------------------------------

    [Fact]
    public async Task Agent_can_detach_an_untouched_checklist_but_not_a_touched_one()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Onboarding", queueIds: new[] { QueueA }, items: 2);
        var view = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);

        await f.Service.DetachAsync(Agent, TicketInA, view.Checklist.Id, default);
        Assert.Empty(f.Repo.Checklists);
        Assert.Contains(f.Tickets.Events, e => e.EventType == "ChecklistDetached");

        var view2 = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);
        await f.Service.SetItemStateAsync(Agent, TicketInA, view2.Items[0].Id, "done", null, null, default);
        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.DetachAsync(Agent, TicketInA, view2.Checklist.Id, default));
        Assert.Equal(ChecklistRejectCode.Forbidden, ex.Code);
        Assert.Single(f.Repo.Checklists);

        await f.Service.DetachAsync(Admin, TicketInA, view2.Checklist.Id, default);
        Assert.Empty(f.Repo.Checklists);
    }

    // ---- item state -----------------------------------------------------

    [Fact]
    public async Task Not_applicable_requires_a_reason_and_invalid_state_is_rejected()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Onboarding", queueIds: new[] { QueueA }, items: 1);
        var view = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);
        var itemId = view.Items[0].Id;

        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.SetItemStateAsync(Agent, TicketInA, itemId, "na", "  ", null, default));
        Assert.Equal(ChecklistRejectCode.Invalid, ex.Code);
        var ex2 = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.SetItemStateAsync(Agent, TicketInA, itemId, "maybe", null, null, default));
        Assert.Equal(ChecklistRejectCode.Invalid, ex2.Code);

        var item = await f.Service.SetItemStateAsync(Agent, TicketInA, itemId, "na", "Customer has no server", null, default);
        Assert.Equal("na", item.State);
    }

    [Fact]
    public async Task Completing_and_reopening_write_timeline_events_and_item_changes_follow_the_setting()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Onboarding", queueIds: new[] { QueueA }, items: 2);
        var view = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);

        await f.Service.SetItemStateAsync(Agent, TicketInA, view.Items[0].Id, "done", null, null, default);
        Assert.DoesNotContain(f.Tickets.Events, e => e.EventType == "ChecklistCompleted");
        Assert.DoesNotContain(f.Tickets.Events, e => e.EventType == "ChecklistItemChanged");

        await f.Service.SetItemStateAsync(Agent, TicketInA, view.Items[1].Id, "na", "n/a here", null, default);
        Assert.Single(f.Tickets.Events, e => e.EventType == "ChecklistCompleted");

        // Same-state call is a no-op: no extra events, no realtime push.
        var pushes = f.Notifier.Notified.Count;
        await f.Service.SetItemStateAsync(Agent, TicketInA, view.Items[1].Id, "na", "again", null, default);
        Assert.Equal(pushes, f.Notifier.Notified.Count);

        f.Settings.LogItemChanges = true;
        await f.Service.SetItemStateAsync(Agent, TicketInA, view.Items[0].Id, "open", null, null, default);
        Assert.Single(f.Tickets.Events, e => e.EventType == "ChecklistReopened");
        Assert.Single(f.Tickets.Events, e => e.EventType == "ChecklistItemChanged");
    }

    // ---- ad-hoc items ---------------------------------------------------

    [Fact]
    public async Task Ad_hoc_items_are_editable_by_their_author_or_an_admin_while_open_only()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Onboarding", queueIds: new[] { QueueA }, items: 1);
        var view = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);
        var templateItem = view.Items[0];

        // Template items: never editable/removable on the ticket.
        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() =>
            f.Service.UpdateItemAsync(Admin, TicketInA, templateItem.Id, new ChecklistItemInput("x", null, null, null, null, null, null), default));
        Assert.Equal(ChecklistRejectCode.Forbidden, ex.Code);

        var added = await f.Service.AddItemAsync(Agent, TicketInA, view.Checklist.Id, null,
            new ChecklistItemInput("Order headsets", "two of them", "Back Office", "Week 2", "https://example.com/manual", "Manual", null), default);
        Assert.True(added.IsAdHoc);
        Assert.Equal(AgentId, added.AddedByUserId);
        Assert.Contains(f.Audit.Rows, r => r.EventType == "ticket.checklist.item_added");

        // Another agent may not edit or remove it; the author and an admin may.
        var ex2 = await Assert.ThrowsAsync<ChecklistRejectedException>(() =>
            f.Service.RemoveItemAsync(OtherAgent, TicketInA, added.Id, default));
        Assert.Equal(ChecklistRejectCode.Forbidden, ex2.Code);

        var edited = await f.Service.UpdateItemAsync(Agent, TicketInA, added.Id,
            new ChecklistItemInput("Order 3 headsets", null, null, null, null, null, null), default);
        Assert.Equal("Order 3 headsets", edited.Title);

        // Once done, even the author cannot remove it without reopening.
        await f.Service.SetItemStateAsync(Agent, TicketInA, added.Id, "done", null, null, default);
        var ex3 = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.RemoveItemAsync(Agent, TicketInA, added.Id, default));
        Assert.Equal(ChecklistRejectCode.Forbidden, ex3.Code);
        await f.Service.SetItemStateAsync(Agent, TicketInA, added.Id, "open", null, null, default);
        await f.Service.RemoveItemAsync(Admin, TicketInA, added.Id, default);
        Assert.DoesNotContain(f.Repo.Items, i => i.Id == added.Id);
    }

    [Fact]
    public async Task Ad_hoc_item_rejects_unsafe_links_and_the_item_cap()
    {
        var f = new Fixture(maxItems: 2);
        var tpl = f.Templates.Add("Onboarding", queueIds: new[] { QueueA }, items: 1);
        var view = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);

        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.AddItemAsync(Agent, TicketInA, view.Checklist.Id, null,
            new ChecklistItemInput("Bad", null, null, null, "javascript:alert(1)", null, null), default));
        Assert.Equal(ChecklistRejectCode.Invalid, ex.Code);

        await f.Service.AddItemAsync(Agent, TicketInA, view.Checklist.Id, null, new ChecklistItemInput("Second", null, null, null, null, null, null), default);
        var ex2 = await Assert.ThrowsAsync<ChecklistRejectedException>(() => f.Service.AddItemAsync(Agent, TicketInA, view.Checklist.Id, null,
            new ChecklistItemInput("Third", null, null, null, null, null, null), default));
        Assert.Equal(ChecklistRejectCode.TooManyItems, ex2.Code);
    }

    [Fact]
    public async Task Item_from_another_ticket_is_not_found_via_this_ticket()
    {
        var f = new Fixture();
        var tpl = f.Templates.Add("Onboarding", queueIds: Array.Empty<Guid>(), items: 1);
        var view = await f.Service.AttachAsync(Agent, TicketInA, tpl.Id, default);
        var otherTicket = f.Mutations.AddTicket(QueueA);
        var ex = await Assert.ThrowsAsync<ChecklistRejectedException>(() =>
            f.Service.SetItemStateAsync(Agent, otherTicket, view.Items[0].Id, "done", null, null, default));
        Assert.Equal(ChecklistRejectCode.NotFound, ex.Code);
    }

    // ---- fixture --------------------------------------------------------

    private sealed class Fixture
    {
        public readonly InMemoryChecklists Repo = new();
        public readonly InMemoryTemplates Templates = new();
        public readonly FakeMutations Mutations = new();
        public readonly RecordingTickets Tickets = new();
        public readonly MutableSettings Settings;
        public readonly RecordingAudit Audit = new();
        public readonly RecordingNotifier Notifier = new();
        public readonly TicketChecklistService Service;

        public Fixture(int maxPerTicket = 10, int maxItems = 300)
        {
            Settings = new MutableSettings { MaxPerTicket = maxPerTicket, MaxItems = maxItems };
            Mutations.AddTicket(QueueA, TicketInA);
            Service = new TicketChecklistService(Repo, Templates, Mutations, Tickets, Settings, Audit, Notifier);
        }
    }

    private sealed class MutableSettings : IChecklistSettingsReader
    {
        public bool Enabled = true;
        public bool LogItemChanges;
        public int MaxPerTicket = 10;
        public int MaxItems = 300;
        public Task<ChecklistRuntimeSettings> GetAsync(CancellationToken ct)
            => Task.FromResult(new ChecklistRuntimeSettings(Enabled, new[] { "Resolved", "Closed" }, LogItemChanges, MaxPerTicket, MaxItems));
    }

    private sealed class RecordingNotifier : ITicketListNotifier
    {
        public readonly List<Guid> Notified = new();
        public Task NotifyUpdatedAsync(Guid ticketId, CancellationToken ct) { Notified.Add(ticketId); return Task.CompletedTask; }
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public readonly List<AuditEvent> Rows = new();
        public Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default) { Rows.Add(evt); return Task.CompletedTask; }
    }

    private sealed class InMemoryTemplates : IChecklistTemplateRepository
    {
        public readonly List<ChecklistTemplateDetail> Rows = new();

        public ChecklistTemplateDetail Add(string name, Guid[] queueIds, int items, bool active = true, bool blockClose = true)
        {
            var def = new ChecklistTemplateDefinition
            {
                Sections = new List<ChecklistTemplateSection>
                {
                    new()
                    {
                        Title = "Main",
                        Items = Enumerable.Range(1, items).Select(i => new ChecklistTemplateItem { Title = $"Step {i}" }).ToList(),
                    },
                },
            };
            var d = new ChecklistTemplateDetail(Guid.NewGuid(), name, "", active, blockClose, queueIds, def, items, DateTime.UtcNow, DateTime.UtcNow);
            Rows.Add(d);
            return d;
        }

        public Task<ChecklistTemplateDetail?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
        public Task<IReadOnlyList<ChecklistTemplateSummary>> ListAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> CreateAsync(ChecklistTemplateInput input, Guid? createdByUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UpdateAsync(Guid id, ChecklistTemplateInput input, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChecklistTemplateSummary>> ListAvailableForQueueAsync(Guid queueId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChecklistTemplateSummary>>(Rows
                .Where(r => r.IsActive && (r.QueueIds.Count == 0 || r.QueueIds.Contains(queueId)))
                .Select(r => new ChecklistTemplateSummary(r.Id, r.Name, r.Description, r.IsActive, r.BlockClose, r.QueueIds, r.ItemCount, r.CreatedUtc, r.UpdatedUtc))
                .ToList());
    }

    /// Minimal stateful stand-in for the Postgres repository: keeps
    /// checklists/items in lists and recomputes the counters the way the
    /// SQL does (required done or n/a → complete).
    private sealed class InMemoryChecklists : ITicketChecklistRepository
    {
        public readonly List<TicketChecklistRow> Checklists = new();
        public readonly List<TicketChecklistSection> Sections = new();
        public readonly List<TicketChecklistItem> Items = new();
        public readonly List<TicketChecklistItemEvent> Events = new();

        public Task<IReadOnlyList<TicketChecklistView>> ListForTicketAsync(Guid ticketId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TicketChecklistView>>(Checklists.Where(c => c.TicketId == ticketId)
                .Select(c => new TicketChecklistView(c,
                    Sections.Where(s => s.ChecklistId == c.Id).ToList(),
                    Items.Where(i => i.ChecklistId == c.Id).OrderBy(i => i.SortOrder).ToList())).ToList());

        public Task<TicketChecklistRow?> GetChecklistAsync(Guid checklistId, CancellationToken ct)
            => Task.FromResult(Checklists.FirstOrDefault(c => c.Id == checklistId));

        public Task<int> CountForTicketAsync(Guid ticketId, CancellationToken ct)
            => Task.FromResult(Checklists.Count(c => c.TicketId == ticketId));

        public Task<int> CountItemsAsync(Guid checklistId, CancellationToken ct)
            => Task.FromResult(Items.Count(i => i.ChecklistId == checklistId));

        public Task<Guid> AttachAsync(Guid ticketId, Guid? templateId, string name, string description, bool blockClose, ChecklistTemplateDefinition definition, Guid userId, CancellationToken ct)
        {
            var c = new TicketChecklistRow { Id = Guid.NewGuid(), TicketId = ticketId, TemplateId = templateId, Name = name, Description = description, BlockClose = blockClose, AttachedByUserId = userId, AttachedUtc = DateTime.UtcNow };
            Checklists.Add(c);
            var order = 0;
            foreach (var s in definition.Sections)
            {
                Guid? sid = null;
                if (s.Title.Length > 0) { var sec = new TicketChecklistSection { Id = Guid.NewGuid(), ChecklistId = c.Id, Title = s.Title }; Sections.Add(sec); sid = sec.Id; }
                foreach (var i in s.Items)
                    Items.Add(new TicketChecklistItem { Id = Guid.NewGuid(), ChecklistId = c.Id, TicketId = ticketId, SectionId = sid, Title = i.Title, IsRequired = i.IsRequired, SortOrder = order++, State = "open" });
            }
            Recount(c);
            return Task.FromResult(c.Id);
        }

        public Task<bool> DetachAsync(Guid checklistId, CancellationToken ct)
        {
            var n = Checklists.RemoveAll(c => c.Id == checklistId);
            Items.RemoveAll(i => i.ChecklistId == checklistId);
            return Task.FromResult(n > 0);
        }

        public Task<TicketChecklistItem?> GetItemAsync(Guid itemId, CancellationToken ct)
            => Task.FromResult(Items.FirstOrDefault(i => i.Id == itemId));

        public Task<ChecklistItemStateChange?> SetItemStateAsync(Guid itemId, string newState, string naReason, string comment, Guid userId, CancellationToken ct)
        {
            var item = Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null) return Task.FromResult<ChecklistItemStateChange?>(null);
            var c = Checklists.First(x => x.Id == item.ChecklistId);
            var wasComplete = c.CompletedUtc is not null;
            if (item.State == newState)
                return Task.FromResult<ChecklistItemStateChange?>(new ChecklistItemStateChange(false, c.Id, c.TicketId, c.Name, item.State, newState, wasComplete, wasComplete));
            var from = item.State;
            item.State = newState; item.NaReason = newState == "na" ? naReason : ""; item.StateChangedByUserId = userId;
            Events.Add(new TicketChecklistItemEvent { ItemId = itemId, Kind = "state_change", FromState = from, ToState = newState, Comment = comment, UserId = userId });
            c.Touched = true;
            Recount(c);
            return Task.FromResult<ChecklistItemStateChange?>(new ChecklistItemStateChange(true, c.Id, c.TicketId, c.Name, from, newState, wasComplete, c.CompletedUtc is not null));
        }

        public Task<bool> AddCommentAsync(Guid itemId, string comment, Guid userId, CancellationToken ct)
        {
            var item = Items.First(i => i.Id == itemId);
            item.CommentCount++;
            Checklists.First(c => c.Id == item.ChecklistId).Touched = true;
            return Task.FromResult(true);
        }

        public Task<Guid?> AddItemAsync(Guid checklistId, Guid? sectionId, ChecklistTemplateItem item, Guid userId, CancellationToken ct)
        {
            var c = Checklists.FirstOrDefault(x => x.Id == checklistId);
            if (c is null) return Task.FromResult<Guid?>(null);
            var row = new TicketChecklistItem { Id = Guid.NewGuid(), ChecklistId = checklistId, TicketId = c.TicketId, SectionId = sectionId, Title = item.Title, Description = item.Description, LinkUrl = item.LinkUrl, IsRequired = item.IsRequired, IsAdHoc = true, AddedByUserId = userId, State = "open", SortOrder = Items.Count };
            Items.Add(row);
            c.Touched = true;
            Recount(c);
            return Task.FromResult<Guid?>(row.Id);
        }

        public Task<bool> UpdateItemAsync(Guid itemId, ChecklistTemplateItem item, Guid userId, CancellationToken ct)
        {
            var row = Items.First(i => i.Id == itemId);
            row.Title = item.Title; row.Description = item.Description; row.LinkUrl = item.LinkUrl;
            return Task.FromResult(true);
        }

        public Task<bool> RemoveItemAsync(Guid itemId, Guid userId, CancellationToken ct)
        {
            var row = Items.First(i => i.Id == itemId);
            Items.Remove(row);
            Recount(Checklists.First(c => c.Id == row.ChecklistId));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<TicketChecklistItemEvent>> ListItemEventsAsync(Guid itemId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TicketChecklistItemEvent>>(Events.Where(e => e.ItemId == itemId).ToList());

        public Task<IReadOnlyList<ChecklistBlocker>> GetBlockersAsync(Guid ticketId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChecklistBlocker>>(Checklists
                .Where(c => c.TicketId == ticketId && c.BlockClose && c.RequiredDone < c.RequiredTotal)
                .Select(c => new ChecklistBlocker(c.Id, c.Name, c.RequiredTotal - c.RequiredDone)).ToList());

        private void Recount(TicketChecklistRow c)
        {
            var items = Items.Where(i => i.ChecklistId == c.Id).ToList();
            c.RequiredTotal = items.Count(i => i.IsRequired);
            c.RequiredDone = items.Count(i => i.IsRequired && i.State != "open");
            c.TotalItems = items.Count;
            c.DoneItems = items.Count(i => i.State != "open");
            var complete = (c.RequiredTotal > 0 && c.RequiredDone == c.RequiredTotal)
                           || (c.RequiredTotal == 0 && c.TotalItems > 0 && c.DoneItems == c.TotalItems);
            c.CompletedUtc = complete ? (c.CompletedUtc ?? DateTime.UtcNow) : null;
        }
    }

    /// Access oracle: a ticket exists in a queue; agents have access to every
    /// registered ticket (queue access itself is covered by the mutation
    /// service's own tests).
    private sealed class FakeMutations : ITicketMutationService
    {
        private readonly Dictionary<Guid, TicketDetail> _tickets = new();

        public Guid AddTicket(Guid queueId, Guid? id = null)
        {
            var tid = id ?? Guid.NewGuid();
            var t = new Ticket(
                tid, 1, "Subject", Guid.NewGuid(), null, queueId, Guid.NewGuid(), Guid.NewGuid(), null,
                "web", null, DateTime.UtcNow, DateTime.UtcNow, null, null, null, null, false);
            _tickets[tid] = new TicketDetail(t, new TicketBody(tid, "body", null), Array.Empty<TicketEvent>(), Array.Empty<TicketEventPin>());
            return tid;
        }

        public Task<AccessPrecheck> PrecheckAccessAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
            => Task.FromResult(_tickets.TryGetValue(ticketId, out var d)
                ? new AccessPrecheck(TicketMutationCheck.Ok, d)
                : new AccessPrecheck(TicketMutationCheck.NotFound, null));

        public Task<FieldUpdatePrecheck> PrecheckFieldUpdateAsync(TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> ApplyFieldUpdateAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task PublishFieldUpdateAsync(TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update, object auditPayload, TriggerChangeSet? changeSet, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct) => throw new NotImplementedException();
        public Task PublishEventAsync(TicketMutationActor actor, Guid ticketId, TicketEvent evt, object auditPayload, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class RecordingTickets : ITicketRepository
    {
        public readonly List<NewTicketEvent> Events = new();
        private long _next = 1;

        public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
        {
            Events.Add(input);
            return Task.FromResult<TicketEvent?>(new TicketEvent(_next++, ticketId, input.EventType, input.AuthorUserId, null, null,
                input.BodyText, input.BodyHtml, input.MetadataJson ?? "{}", input.IsInternal, DateTime.UtcNow, null, null));
        }

        public Task<TicketDetail?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketPage> SearchAsync(TicketQuery query, VisibilityScope scope, Guid? viewerUserId, Guid? viewerCompanyId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Ticket> CreateAsync(NewTicket input, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> UpdateFieldsAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> AssignCompanyAsync(Guid ticketId, Guid companyId, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetail?> ChangeRequesterAsync(Guid ticketId, Guid newContactId, Guid? newCompanyId, bool awaitingCompanyAssignment, string? companyResolvedVia, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEvent?> UpdateEventAsync(Guid ticketId, long eventId, UpdateTicketEvent input, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketEventRevision>> GetEventRevisionsAsync(Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> PinEventAsync(Guid ticketId, long eventId, Guid userId, string remark, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UnpinEventAsync(Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketEventPin?> UpdatePinRemarkAsync(Guid ticketId, long eventId, string remark, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> EventBelongsToTicketAsync(Guid ticketId, long eventId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, int>> GetOpenCountsByQueueAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> InsertFakeBatchAsync(int count, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> IsTitleReviewedAsync(Guid ticketId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkTitleReviewedAsync(Guid ticketId, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketPickerHit>> SearchPickerAsync(string? search, Guid excludeTicketId, IReadOnlyCollection<Guid>? accessibleQueueIds, Guid? recentForUserId, int limit, CancellationToken ct, bool projectsOnly = false) => throw new NotImplementedException();
        public Task<MergeResult?> MergeAsync(Guid sourceTicketId, Guid targetTicketId, Guid actorUserId, bool acknowledgedCrossCustomer, CancellationToken ct) => throw new NotImplementedException();
        public Task<SplitResult?> SplitAsync(Guid sourceTicketId, long sourceMailEventId, string newSubject, Guid actorUserId, string? overrideBodyHtml, string? overrideBodyText, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketDetailRelations?> GetDetailRelationsAsync(Guid ticketId, CancellationToken ct) => throw new NotImplementedException();
        public Task<LinkParentResult> LinkParentAsync(Guid ticketId, Guid parentTicketId, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UnlinkParentAsync(Guid ticketId, Guid actorUserId, CancellationToken ct) => throw new NotImplementedException();
    }
}
