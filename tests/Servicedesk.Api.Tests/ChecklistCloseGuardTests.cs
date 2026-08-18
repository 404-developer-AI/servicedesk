using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.103 — pins the close-block rule: only an enabled feature, a target
/// status inside the configured blocking categories, and a blocking
/// checklist with required open items yields blockers. Everything else lets
/// the status change through untouched.
public sealed class ChecklistCloseGuardTests
{
    private static readonly Guid TicketId = Guid.NewGuid();
    private static readonly Guid ResolvedId = Guid.NewGuid();
    private static readonly Guid PendingId = Guid.NewGuid();

    [Fact]
    public async Task Blocks_when_enabled_category_matches_and_a_checklist_is_open()
    {
        var guard = Build(enabled: true, categories: "Resolved,Closed", blockers: 1);
        var result = await guard.FindBlockersAsync(TicketId, ResolvedId, default);
        var b = Assert.Single(result);
        Assert.Equal("Onboarding", b.Name);
        Assert.Equal(3, b.OpenRequired);
    }

    [Fact]
    public async Task Does_not_block_when_feature_is_off()
    {
        var guard = Build(enabled: false, categories: "Resolved,Closed", blockers: 1);
        Assert.Empty(await guard.FindBlockersAsync(TicketId, ResolvedId, default));
    }

    [Fact]
    public async Task Does_not_block_for_a_status_outside_the_blocking_categories()
    {
        var guard = Build(enabled: true, categories: "Resolved,Closed", blockers: 1);
        Assert.Empty(await guard.FindBlockersAsync(TicketId, PendingId, default));
    }

    [Fact]
    public async Task Closed_only_setting_lets_a_resolve_through()
    {
        var guard = Build(enabled: true, categories: "Closed", blockers: 1);
        Assert.Empty(await guard.FindBlockersAsync(TicketId, ResolvedId, default));
    }

    [Fact]
    public async Task No_open_blocking_checklist_means_no_block()
    {
        var guard = Build(enabled: true, categories: "Resolved,Closed", blockers: 0);
        Assert.Empty(await guard.FindBlockersAsync(TicketId, ResolvedId, default));
    }

    [Fact]
    public void Category_parser_drops_unknown_tokens_and_is_case_insensitive()
    {
        var parsed = ChecklistSettingsReader.ParseCategories(" closed , Open,RESOLVED,, bogus ");
        Assert.Equal(new[] { "Closed", "Resolved" }, parsed);
        Assert.Empty(ChecklistSettingsReader.ParseCategories(""));
        Assert.Empty(ChecklistSettingsReader.ParseCategories(null));
    }

    private static ChecklistCloseGuard Build(bool enabled, string categories, int blockers)
        => new(new FakeChecklists(blockers), new FakeTaxonomy(), new FakeSettings(enabled, categories));

    private sealed class FakeSettings : IChecklistSettingsReader
    {
        private readonly ChecklistRuntimeSettings _s;
        public FakeSettings(bool enabled, string categories)
            => _s = new ChecklistRuntimeSettings(enabled, ChecklistSettingsReader.ParseCategories(categories), false, 10, 300);
        public Task<ChecklistRuntimeSettings> GetAsync(CancellationToken ct) => Task.FromResult(_s);
    }

    private sealed class FakeChecklists : ITicketChecklistRepository
    {
        private readonly int _blockers;
        public FakeChecklists(int blockers) => _blockers = blockers;

        public Task<IReadOnlyList<ChecklistBlocker>> GetBlockersAsync(Guid ticketId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChecklistBlocker>>(
                Enumerable.Range(0, _blockers).Select(_ => new ChecklistBlocker(Guid.NewGuid(), "Onboarding", 3)).ToList());

        public Task<IReadOnlyList<TicketChecklistView>> ListForTicketAsync(Guid ticketId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketChecklistRow?> GetChecklistAsync(Guid checklistId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountForTicketAsync(Guid ticketId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountItemsAsync(Guid checklistId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> AttachAsync(Guid ticketId, Guid? templateId, string name, string description, bool blockClose, ChecklistTemplateDefinition definition, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DetachAsync(Guid checklistId, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketChecklistItem?> GetItemAsync(Guid itemId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChecklistItemStateChange?> SetItemStateAsync(Guid itemId, string newState, string naReason, string comment, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> AddCommentAsync(Guid itemId, string comment, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid?> AddItemAsync(Guid checklistId, Guid? sectionId, ChecklistTemplateItem item, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> UpdateItemAsync(Guid itemId, ChecklistTemplateItem item, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RemoveItemAsync(Guid itemId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketChecklistItemEvent>> ListItemEventsAsync(Guid itemId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeTaxonomy : ITaxonomyRepository
    {
        public Task<Status?> GetStatusAsync(Guid id, CancellationToken ct)
        {
            Status? s = id == ResolvedId
                ? new Status(id, "Resolved", "resolved", "Resolved", "#0f0", "check", 0, true, true, false, DateTime.UtcNow, DateTime.UtcNow)
                : id == PendingId
                    ? new Status(id, "Pending", "pending", "Pending", "#ff0", "clock", 0, true, true, false, DateTime.UtcNow, DateTime.UtcNow)
                    : null;
            return Task.FromResult(s);
        }

        public Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct) => throw new NotImplementedException();
        public Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string desc, string color, string icon, int sortOrder, bool isActive, string? inbound, string? outbound, string? inboundFolderId, string? inboundFolderName, IReadOnlyList<Guid> allowedStatusIds, Guid? defaultStatusId, bool aiAssistEnabled, string timeAlertMode, int? timeAlertThresholdMinutes, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteQueueAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetQueueInboundPollingAsync(Guid id, bool enabled, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketType>> ListTicketTypesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType?> GetTicketTypeAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType?> GetTicketTypeByCodeAsync(string code, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType> CreateTicketTypeAsync(TicketType t, CancellationToken ct) => throw new NotImplementedException();
        public Task<TicketType?> UpdateTicketTypeAsync(Guid id, string code, string label, string description, string icon, string color, int sortOrder, bool isActive, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteTicketTypeAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Priority>> ListPrioritiesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> GetPriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority> CreatePriorityAsync(Priority p, CancellationToken ct) => throw new NotImplementedException();
        public Task<Priority?> UpdatePriorityAsync(Guid id, string name, string slug, int level, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeletePriorityAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Status>> ListStatusesAsync(CancellationToken ct) => throw new NotImplementedException();
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
