using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Views;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Insights.Rewind;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.31 — Insights Rewind: view → query mapping, the server port of the
/// grouped list layout, change-only capture, gap coverage, and the access
/// rules (flag, view access, queue access on both tickets and counts).
public sealed class InsightsRewindTests
{
    private static readonly Guid QueueA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid QueueB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid StatusOpen = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid StatusWfp = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid StatusPending = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid ViewId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private static readonly RewindLayoutContext NoTaxonomy =
        new(RewindGroupSort.Off, "#22c55e", "#3b82f6", null);

    // ---- view → query -------------------------------------------------------

    [Fact]
    public void Legacy_singular_filters_fold_into_lists_and_bad_ids_are_dropped()
    {
        var filters = $$"""{"queueId":"{{QueueA}}","statusIds":["nope","{{StatusOpen}}"],"openOnly":true,"callbacksOnly":true}""";
        var q = RewindViewQuery.Build(filters, RewindDisplayConfig.Empty, 500, "Ticket#");

        Assert.Equal(new[] { QueueA }, q.QueueIds);
        Assert.Equal(new[] { StatusOpen }, q.StatusIds);
        Assert.True(q.OpenOnly);
        Assert.True(q.CallbacksOnly);
        Assert.Equal(500, q.Limit);
        Assert.Null(q.AccessibleQueueIds); // capture is unscoped; access applies on read
    }

    [Fact]
    public void Display_config_carries_sort_floats_and_grouping()
    {
        var dc = RewindViewQuery.ParseDisplayConfig(
            """{"priorityFloat":true,"callbackFloat":true,"groupBy":"statusId","groupOrder":["x"],"sort":{"field":"createdUtc","direction":"asc"},"stateBucketSort":true}""");
        var q = RewindViewQuery.Build("{}", dc, 100, null);

        Assert.True(q.PriorityFloat);
        Assert.True(q.CallbackFloat);
        Assert.False(q.ResearchFloat);
        Assert.True(q.StateBucketSort);
        Assert.Equal("createdUtc", q.SortField);
        Assert.Equal("asc", q.SortDirection);
        Assert.Equal("statusId", dc.GroupBy);
        Assert.Equal(new[] { "x" }, dc.GroupOrder);
    }

    [Fact]
    public void Bad_json_falls_back_to_the_plain_list()
    {
        Assert.Equal(RewindDisplayConfig.Empty, RewindViewQuery.ParseDisplayConfig("{not json"));
        var q = RewindViewQuery.Build("[1,2", RewindDisplayConfig.Empty, 10, null);
        Assert.Null(q.QueueIds);
    }

    // ---- layout (lockstep with GroupedTicketList) ---------------------------

    [Fact]
    public void Floats_come_first_in_fixed_order_then_status_groups()
    {
        var items = new[]
        {
            Ticket(1, StatusOpen, "Open"),
            Ticket(2, StatusWfp, "Open", name: "Waiting for pickup"),
            Ticket(3, StatusOpen, "Open", research: true),
            Ticket(4, StatusOpen, "Open", callback: true),
            Ticket(5, StatusOpen, "Open", priorityDefault: false),
            Ticket(6, StatusPending, "Pending", callback: true), // only New/Open float
        };
        var dc = RewindDisplayConfig.Empty with
        {
            PriorityFloat = true, CallbackFloat = true, ResearchFloat = true, GroupBy = "statusId",
        };
        var taxonomy = new Dictionary<string, int>
        {
            [StatusWfp.ToString()] = 1, [StatusOpen.ToString()] = 2, [StatusPending.ToString()] = 3,
        };

        var groups = RewindArranger.Arrange(items, dc, NoTaxonomy with { TaxonomySortOrder = taxonomy });

        Assert.Equal(
            new[] { RewindArranger.KeyPriority, RewindArranger.KeyCallback, RewindArranger.KeyResearch,
                StatusWfp.ToString(), StatusOpen.ToString(), StatusPending.ToString() },
            groups.Select(g => g.Key));
        Assert.Equal(new long[] { 5 }, groups[0].Items.Select(t => t.Number));
        Assert.Equal(new long[] { 4 }, groups[1].Items.Select(t => t.Number));
        Assert.Equal(new long[] { 3 }, groups[2].Items.Select(t => t.Number));
        Assert.Equal("Waiting for pickup", groups[3].Label);
        Assert.Equal(new long[] { 6 }, groups[5].Items.Select(t => t.Number));
    }

    [Fact]
    public void View_group_order_wins_over_taxonomy_order()
    {
        var items = new[] { Ticket(1, StatusOpen, "Open"), Ticket(2, StatusWfp, "Open") };
        var dc = RewindDisplayConfig.Empty with { GroupBy = "statusId", GroupOrder = new[] { StatusOpen.ToString() } };
        var taxonomy = new Dictionary<string, int> { [StatusWfp.ToString()] = 1, [StatusOpen.ToString()] = 2 };

        var groups = RewindArranger.Arrange(items, dc, NoTaxonomy with { TaxonomySortOrder = taxonomy });

        Assert.Equal(new[] { StatusOpen.ToString(), StatusWfp.ToString() }, groups.Select(g => g.Key));
    }

    [Fact]
    public void Per_state_group_sort_reorders_inside_status_groups_with_blanks_last()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            Ticket(1, StatusPending, "Pending", pendingTill: null),
            Ticket(2, StatusPending, "Pending", pendingTill: now.AddHours(3)),
            Ticket(3, StatusPending, "Pending", pendingTill: now.AddHours(1)),
        };
        var dc = RewindDisplayConfig.Empty with { GroupBy = "statusId" };
        var ctx = NoTaxonomy with { GroupSort = new RewindGroupSort(true, "pendingTillUtc", "asc", "updatedUtc", "asc") };

        var groups = RewindArranger.Arrange(items, dc, ctx);

        Assert.Equal(new long[] { 3, 2, 1 }, groups.Single().Items.Select(t => t.Number));
    }

    [Fact]
    public void No_grouping_and_no_floats_is_one_unlabelled_group_in_query_order()
    {
        var items = new[] { Ticket(2, StatusOpen, "Open"), Ticket(1, StatusOpen, "Open") };
        var groups = RewindArranger.Arrange(items, RewindDisplayConfig.Empty, NoTaxonomy);

        var g = Assert.Single(groups);
        Assert.Equal(RewindArranger.KeyAll, g.Key);
        Assert.Equal(new long[] { 2, 1 }, g.Items.Select(t => t.Number));
    }

    // ---- capture --------------------------------------------------------------

    [Fact]
    public void Capture_hash_ignores_last_activity_but_sees_a_subject_change()
    {
        var slot = new DateTime(2026, 10, 8, 12, 45, 0, DateTimeKind.Utc);
        var a = Ticket(1, StatusOpen, "Open");
        var touched = a with { UpdatedUtc = a.UpdatedUtc.AddMinutes(5) };
        var renamed = a with { Subject = "Printer offline on floor 2" };

        string Hash(TicketListItem t) => RewindCaptureService.BuildCapture(ViewId, slot, 15,
            RewindArranger.Arrange(new[] { t }, RewindDisplayConfig.Empty, NoTaxonomy), false).ContentHash;

        Assert.Equal(Hash(a), Hash(touched));
        Assert.NotEqual(Hash(a), Hash(renamed));
    }

    [Fact]
    public void Capture_counts_per_group_and_queue()
    {
        var slot = new DateTime(2026, 10, 8, 12, 45, 0, DateTimeKind.Utc);
        var items = new[]
        {
            Ticket(1, StatusOpen, "Open", queue: QueueA),
            Ticket(2, StatusOpen, "Open", queue: QueueB),
            Ticket(3, StatusOpen, "Open", queue: QueueA),
        };
        var capture = RewindCaptureService.BuildCapture(ViewId, slot, 15,
            RewindArranger.Arrange(items, RewindDisplayConfig.Empty with { GroupBy = "statusId" }, NoTaxonomy), true);

        var counts = JsonSerializer.Deserialize<List<RewindCount>>(capture.CountsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(counts, c => c.G == StatusOpen.ToString() && c.Q == QueueA && c.N == 2);
        Assert.Contains(counts, c => c.G == StatusOpen.ToString() && c.Q == QueueB && c.N == 1);
        Assert.Equal(3, capture.TicketCount);
        Assert.True(capture.Truncated);
    }

    [Fact]
    public async Task Unchanged_capture_extends_the_latest_row_instead_of_inserting()
    {
        var store = new FakeStore();
        IReadOnlyList<TicketListItem> current = new[] { Ticket(1, StatusOpen, "Open") };
        // The capture only ever lists tickets and, for taxonomy grouping,
        // reads sort orders — every other repository member throws.
        var tickets = PartialFake<ITicketRepository>.Create((m, _) => m.Name == nameof(ITicketRepository.SearchAsync)
            ? Task.FromResult(new TicketPage(current, null, null))
            : throw new NotSupportedException(m.Name));
        var taxonomy = PartialFake<Servicedesk.Infrastructure.Persistence.Taxonomy.ITaxonomyRepository>.Create(
            (m, _) => throw new NotSupportedException(m.Name));
        var svc = new RewindCaptureService(store,
            new RewindLayoutService(tickets, taxonomy, new InMemorySettingsService()),
            new ThrowingLogger());
        store.Tracked.Add(new RewindTrackedView(ViewId, "Servicedesk", "{}", "{}"));
        var t0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(1, await svc.CaptureAsync(t0, 15, default));
        Assert.Equal(0, await svc.CaptureAsync(t0.AddMinutes(15), 15, default));
        current = new[] { Ticket(1, StatusWfp, "Open") };
        Assert.Equal(1, await svc.CaptureAsync(t0.AddMinutes(30), 15, default));

        Assert.Equal(2, store.Rows.Count);
        Assert.Equal(t0.AddMinutes(15), store.Rows[0].LastSeenUtc);
    }

    [Fact]
    public async Task Changed_capture_records_arrivals_and_leavers_with_their_reason()
    {
        var store = new FakeStore();
        IReadOnlyList<TicketListItem> current = new[]
        {
            Ticket(1, StatusOpen, "Open"), Ticket(2, StatusOpen, "Open"), Ticket(3, StatusOpen, "Open"),
        };
        var tickets = PartialFake<ITicketRepository>.Create((m, _) => m.Name == nameof(ITicketRepository.SearchAsync)
            ? Task.FromResult(new TicketPage(current, null, null))
            : throw new NotSupportedException(m.Name));
        var taxonomy = PartialFake<Servicedesk.Infrastructure.Persistence.Taxonomy.ITaxonomyRepository>.Create(
            (m, _) => throw new NotSupportedException(m.Name));
        var svc = new RewindCaptureService(store,
            new RewindLayoutService(tickets, taxonomy, new InMemorySettingsService()), new ThrowingLogger());
        store.Tracked.Add(new RewindTrackedView(ViewId, "Servicedesk", "{}", "{}"));
        var t0 = RewindSlots.Floor(DateTime.UtcNow, 15).AddMinutes(-30);

        await svc.CaptureAsync(t0, 15, default);
        Assert.Null(store.Rows[0].Capture.AddedJson); // a view's first row has nothing to compare with

        store.LeaveReasons[Ticket(2, StatusOpen, "Open").Id] = RewindLeaveReason.Closed;
        current = new[] { Ticket(1, StatusOpen, "Open"), Ticket(4, StatusOpen, "Open") }; // 2 closed, 3 gone, 4 new
        await svc.CaptureAsync(t0.AddMinutes(15), 15, default);
        await svc.CaptureAsync(t0.AddMinutes(30), 15, default); // unchanged → extends that row

        var rewind = new RewindService(store, new AllowAllViews(), new InMemorySettingsService(), TimeProvider.System);
        var series = await rewind.GetSeriesAsync(ViewId, null, TimeSpan.FromHours(1), QueueAccessScope.AdminScope);
        var changed = series.Slots.Single(sl => sl.T == t0.AddMinutes(15));
        Assert.Equal(1, changed.Added);
        Assert.Equal(new RewindLeftCounts(1, 0, 1), changed.Left);
        Assert.Null(series.Slots.Single(sl => sl.T == t0).Added);

        var snap = await rewind.GetSnapshotAsync(ViewId, t0.AddMinutes(15), QueueAccessScope.AdminScope);
        Assert.Equal(new long[] { 4 }, snap.Changes!.Added.Select(c => c.N));
        Assert.Equal(new[] { (2L, "closed"), (3L, "other") }, snap.Changes.Removed.Select(c => (c.N, c.R!)));

        // The next unchanged slot reports no change; foreign queues are cut out.
        var later = await rewind.GetSnapshotAsync(ViewId, t0.AddMinutes(30), QueueAccessScope.AdminScope);
        Assert.Empty(later.Changes!.Added);
        var foreign = await rewind.GetSnapshotAsync(ViewId, t0.AddMinutes(15), new QueueAccessScope(false, new HashSet<Guid> { QueueB }));
        Assert.Empty(foreign.Changes!.Added);
        Assert.Empty(foreign.Changes.Removed);
    }

    // ---- coverage -------------------------------------------------------------

    [Fact]
    public void A_row_covers_until_one_interval_after_it_was_last_confirmed()
    {
        var t = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(RewindService.Covers(t, t.AddMinutes(30), 15, t.AddMinutes(30)));
        Assert.True(RewindService.Covers(t, t.AddMinutes(30), 15, t.AddMinutes(44)));
        Assert.False(RewindService.Covers(t, t.AddMinutes(30), 15, t.AddMinutes(45))); // app was down
        Assert.False(RewindService.Covers(t, t.AddMinutes(30), 15, t.AddMinutes(-15)));
    }

    [Fact]
    public async Task Series_counts_and_legend_only_include_visible_queues()
    {
        var store = new FakeStore();
        var now = DateTime.UtcNow;
        var slot = RewindSlots.Floor(now, 15);
        store.Rows.Add(Row(slot.AddHours(-1), slot, new[]
        {
            Ticket(1, StatusOpen, "Open", queue: QueueA),
            Ticket(2, StatusWfp, "Open", queue: QueueB, name: "Secret queue only"),
        }));
        var svc = new RewindService(store, new AllowAllViews(), new InMemorySettingsService(), TimeProvider.System);
        var scope = new QueueAccessScope(false, new HashSet<Guid> { QueueA });

        var series = await svc.GetSeriesAsync(ViewId, null, TimeSpan.FromHours(4), scope);

        Assert.Equal(slot, series.ToUtc);
        Assert.Equal(new[] { StatusOpen.ToString() }, series.Groups.Select(g => g.Key));
        var covered = series.Slots.Where(s => s.Covered).ToList();
        Assert.Equal(5, covered.Count); // -60, -45, -30, -15, 0
        Assert.All(covered, s => Assert.Equal(1, s.Counts.Values.Sum()));
        Assert.False(series.Slots.First().Covered); // before the first capture
    }

    [Fact]
    public async Task Snapshot_hides_tickets_from_foreign_queues_and_flags_deleted_ones()
    {
        var store = new FakeStore();
        var slot = RewindSlots.Floor(DateTime.UtcNow, 15);
        var visible = Ticket(1, StatusOpen, "Open", queue: QueueA);
        store.Rows.Add(Row(slot, slot, new[] { visible, Ticket(2, StatusWfp, "Open", queue: QueueB) }));
        store.Deleted.Add(visible.Id);
        var svc = new RewindService(store, new AllowAllViews(), new InMemorySettingsService(), TimeProvider.System);

        var snap = await svc.GetSnapshotAsync(ViewId, slot, new QueueAccessScope(false, new HashSet<Guid> { QueueA }));

        Assert.True(snap.Covered);
        var item = Assert.Single(snap.Items);
        Assert.Equal(1, item.Number);
        Assert.Equal(new[] { StatusOpen.ToString() }, snap.Groups.Select(g => g.Key));
        Assert.Equal(new[] { visible.Id }, snap.DeletedIds);

        var none = await svc.GetSnapshotAsync(ViewId, slot, new QueueAccessScope(false, new HashSet<Guid>()));
        Assert.Empty(none.Items);
        Assert.Empty(none.Groups);
    }

    // ---- endpoints ------------------------------------------------------------

    [Theory]
    [InlineData("/api/insights/rewind/views")]
    [InlineData("/api/insights/rewind/cccccccc-0000-0000-0000-000000000001/series")]
    [InlineData("/api/insights/rewind/cccccccc-0000-0000-0000-000000000001/snapshot")]
    public async Task Agent_without_insights_flag_gets_403(string url)
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, viewAccess: true, out _);
        var client = await ClientAsync(factory, host, "Agent", insights: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task View_without_access_is_404()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, viewAccess: false, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        var response = await client.GetAsync($"/api/insights/rewind/{ViewId}/snapshot");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(stub.LastScope);
    }

    [Fact]
    public async Task Series_passes_the_callers_queue_scope_and_rejects_unknown_ranges()
    {
        using var factory = new SecurityBaselineFactory();
        using var host = WithStub(factory, viewAccess: true, out var stub);
        var client = await ClientAsync(factory, host, "Agent", insights: true);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/insights/rewind/{ViewId}/series?range=30d")).StatusCode);

        var ok = await client.GetAsync($"/api/insights/rewind/{ViewId}/series?range=4h");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(stub.LastScope!.IsAdmin);
        Assert.Equal(new[] { QueueA }, stub.LastScope.ToArray());
        Assert.Equal(TimeSpan.FromHours(4), stub.LastSpan);
    }

    // ---- helpers ----------------------------------------------------------------

    private static TicketListItem Ticket(
        long number, Guid status, string category, string? name = null, Guid? queue = null,
        bool callback = false, bool research = false, bool priorityDefault = true, DateTime? pendingTill = null)
    {
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(number);
        return new TicketListItem(
            Id: new Guid($"dddddddd-0000-0000-0000-{number:D12}"), Number: number, Subject: $"Ticket {number}",
            QueueId: queue ?? QueueA, QueueName: queue == QueueB ? "Back office" : "Servicedesk",
            StatusId: status, StatusName: name ?? category, StatusColor: "#888888", StatusStateCategory: category,
            PriorityId: Guid.Empty, PriorityName: priorityDefault ? "Normal" : "High", PriorityLevel: priorityDefault ? 2 : 4,
            PriorityColor: "#999999", PriorityIsDefault: priorityDefault,
            RequesterContactId: Guid.Empty, RequesterEmail: "requester@example.test",
            RequesterFirstName: "Alex", RequesterLastName: "Doe",
            RequesterCompanyId: null, CompanyName: "Example Ltd", AssigneeUserId: null, AssigneeEmail: null,
            CategoryId: null, CategoryName: null, CreatedUtc: created, UpdatedUtc: created, DueUtc: null,
            PendingTillUtc: pendingTill, IsCallback: callback, IsResearch: research);
    }

    private static StoredRow Row(DateTime captured, DateTime lastSeen, IReadOnlyList<TicketListItem> items)
    {
        var groups = RewindArranger.Arrange(items, RewindDisplayConfig.Empty with { GroupBy = "statusId" }, NoTaxonomy);
        var c = RewindCaptureService.BuildCapture(ViewId, captured, 15, groups, false);
        return new StoredRow(1, c) { LastSeenUtc = lastSeen };
    }

    private sealed class StoredRow(long id, RewindCapture capture)
    {
        public long Id { get; } = id;
        public RewindCapture Capture { get; } = capture;
        public DateTime LastSeenUtc { get; set; } = capture.SlotUtc;
    }

    private sealed class FakeStore : IRewindStore
    {
        public List<RewindTrackedView> Tracked { get; } = new();
        public List<StoredRow> Rows { get; } = new();
        public HashSet<Guid> Deleted { get; } = new();
        public Dictionary<Guid, string> LeaveReasons { get; } = new();

        public Task<IReadOnlyList<RewindTrackedView>> ListTrackedViewsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RewindTrackedView>>(Tracked);

        public Task<RewindLatest?> GetLatestAsync(Guid viewId, CancellationToken ct)
        {
            var r = Rows.Where(x => x.Capture.ViewId == viewId).MaxBy(x => x.Capture.SlotUtc);
            return Task.FromResult(r is null ? null
                : new RewindLatest(r.Id, r.Capture.SlotUtc, r.LastSeenUtc, r.Capture.IntervalMinutes, r.Capture.ContentHash));
        }

        public Task InsertAsync(RewindCapture capture, CancellationToken ct)
        {
            Rows.Add(new StoredRow(Rows.Count + 1, capture));
            return Task.CompletedTask;
        }

        public Task TouchAsync(long id, DateTime slotUtc, CancellationToken ct)
        {
            Rows.Single(r => r.Id == id).LastSeenUtc = slotUtc;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RewindSeriesRow>> GetSeriesAsync(Guid viewId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RewindSeriesRow>>(Rows
                .Where(r => r.Capture.SlotUtc <= toUtc && r.LastSeenUtc.AddMinutes(r.Capture.IntervalMinutes) > fromUtc)
                .OrderBy(r => r.Capture.SlotUtc)
                .Select(r => new RewindSeriesRow(r.Capture.SlotUtc, r.LastSeenUtc, r.Capture.IntervalMinutes,
                    r.Capture.GroupsJson, r.Capture.CountsJson, r.Capture.AddedJson, r.Capture.RemovedJson))
                .ToList());

        public Task<RewindSnapshotRow?> GetAtAsync(Guid viewId, DateTime atUtc, CancellationToken ct)
        {
            var r = Rows.Where(x => x.Capture.SlotUtc <= atUtc).MaxBy(x => x.Capture.SlotUtc);
            return Task.FromResult(r is null ? null
                : new RewindSnapshotRow(r.Capture.SlotUtc, r.LastSeenUtc, r.Capture.IntervalMinutes,
                    r.Capture.Truncated, r.Capture.GroupsJson, r.Capture.ItemsJson,
                    r.Capture.AddedJson, r.Capture.RemovedJson));
        }

        public Task<string?> GetItemsAsync(long id, CancellationToken ct)
            => Task.FromResult<string?>(Rows.SingleOrDefault(r => r.Id == id)?.Capture.ItemsJson);

        public Task<IReadOnlyDictionary<Guid, string>> ClassifyLeaversAsync(
            IReadOnlyCollection<Guid> ticketIds, DateTime sinceUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                ticketIds.ToDictionary(id => id, id => LeaveReasons.GetValueOrDefault(id, RewindLeaveReason.Other)));

        public Task<IReadOnlySet<Guid>> GetDeletedAsync(IReadOnlyCollection<Guid> ticketIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(ticketIds.Where(Deleted.Contains).ToHashSet());
    }

    /// Surfaces an exception the capture would otherwise log and skip.
    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger<RewindCaptureService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) throw new InvalidOperationException("Capture logged a failure.", exception);
        }
    }

    /// Interface fake that routes every call through one handler — for the
    /// wide repositories where a test only needs one or two members.
    public class PartialFake<T> : global::System.Reflection.DispatchProxy where T : class
    {
        private Func<global::System.Reflection.MethodInfo, object?[]?, object?> _handler = null!;

        public static T Create(Func<global::System.Reflection.MethodInfo, object?[]?, object?> handler)
        {
            var proxy = Create<T, PartialFake<T>>();
            ((PartialFake<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(global::System.Reflection.MethodInfo? targetMethod, object?[]? args)
            => _handler(targetMethod!, args);
    }

    private sealed class AllowAllViews : IViewAccessService
    {
        public Task<IReadOnlyList<View>> GetAccessibleViewsAsync(Guid userId, string role, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<View>>(Array.Empty<View>());
        public Task<bool> HasViewAccessAsync(Guid userId, string role, Guid viewId, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<IReadOnlyList<Guid>> GetDirectViewIdsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public Task SetDirectViewAccessAsync(Guid userId, IReadOnlyList<Guid> viewIds, CancellationToken ct = default)
            => Task.CompletedTask;
        public void InvalidateCache(Guid userId) { }
        public void InvalidateAllViewCaches() { }
    }

    private sealed class StubRewind(bool viewAccess) : IRewindService
    {
        public QueueAccessScope? LastScope { get; private set; }
        public TimeSpan? LastSpan { get; private set; }

        public Task<IReadOnlyList<RewindViewSummary>> ListViewsAsync(Guid userId, string role, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RewindViewSummary>>(Array.Empty<RewindViewSummary>());
        public Task<bool> CanReadViewAsync(Guid userId, string role, Guid viewId, CancellationToken ct = default)
            => Task.FromResult(viewAccess);
        public Task<int> GetIntervalAsync(CancellationToken ct = default) => Task.FromResult(15);

        public Task<RewindSeries> GetSeriesAsync(Guid viewId, DateTime? endUtc, TimeSpan span, QueueAccessScope scope, CancellationToken ct = default)
        {
            LastScope = scope;
            LastSpan = span;
            var t = RewindSlots.Floor(DateTime.UtcNow, 15);
            return Task.FromResult(new RewindSeries(15, t, t, t, Array.Empty<RewindGroup>(), Array.Empty<RewindSlot>()));
        }

        public Task<RewindSnapshot> GetSnapshotAsync(Guid viewId, DateTime atUtc, QueueAccessScope scope, CancellationToken ct = default)
        {
            LastScope = scope;
            return Task.FromResult(new RewindSnapshot(atUtc, false, null, false,
                Array.Empty<RewindGroup>(), Array.Empty<RewindItem>(), Array.Empty<Guid>()));
        }
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithStub(
        SecurityBaselineFactory factory, bool viewAccess, out StubRewind stub)
    {
        var s = new StubRewind(viewAccess);
        stub = s;
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRewindService>();
            services.AddSingleton<IRewindService>(s);
            services.RemoveAll<IQueueAccessService>();
            services.AddSingleton<IQueueAccessService>(new OnlyQueueA());
        }));
    }

    private static async Task<HttpClient> ClientAsync(
        SecurityBaselineFactory factory,
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        string role, bool insights)
    {
        var userId = Guid.NewGuid();
        factory.Sessions.Roles[userId] = role;
        if (insights) factory.Users.InsightsEnabled[userId] = true;
        var sessionId = await factory.Sessions.CreateAsync(
            userId, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1), amr: "pwd");
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }

    private sealed class OnlyQueueA : IQueueAccessService
    {
        public Task<IReadOnlyList<Guid>> GetAccessibleQueueIdsAsync(Guid userId, string role, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(new[] { QueueA });
        public Task<bool> HasQueueAccessAsync(Guid userId, string role, Guid queueId, CancellationToken ct = default)
            => Task.FromResult(queueId == QueueA);
        public Task SetQueueAccessAsync(Guid userId, IReadOnlyList<Guid> queueIds, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<Guid>> GetUsersForQueueAsync(Guid queueId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public void InvalidateCache(Guid userId) { }
    }
}
