using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.103 — template document validation + the two search sources'
/// availability gates (customers get nothing; templates are admin-only).
public sealed class ChecklistTemplateValidatorTests
{
    private static ChecklistTemplateDefinition Def(params string[] titles) => new()
    {
        Sections = new List<ChecklistTemplateSection>
        {
            new() { Title = " Week 1 ", Items = titles.Select(t => new ChecklistTemplateItem { Title = t }).ToList() },
        },
    };

    [Fact]
    public void Blank_items_are_dropped_and_titles_trimmed()
    {
        var def = Def("  Create prospect ", "", "   ", "Book kickoff");
        Assert.Null(ChecklistTemplateValidator.ValidateAndNormalize(def, 300));
        Assert.Equal(new[] { "Create prospect", "Book kickoff" }, def.Sections[0].Items.Select(i => i.Title));
        Assert.Equal("Week 1", def.Sections[0].Title);
        Assert.Equal(2, def.ItemCount);
    }

    [Fact]
    public void At_least_one_item_is_required_and_the_cap_is_enforced()
    {
        Assert.Equal("Add at least one item.", ChecklistTemplateValidator.ValidateAndNormalize(Def("", " "), 300));
        var err = ChecklistTemplateValidator.ValidateAndNormalize(Def("a", "b", "c"), 2);
        Assert.NotNull(err);
        Assert.Contains("at most 2 items", err);
    }

    [Fact]
    public void Links_must_be_absolute_http_or_https()
    {
        var bad = new ChecklistTemplateItem { Title = "x", LinkUrl = "javascript:alert(1)" };
        Assert.NotNull(ChecklistTemplateValidator.ValidateItem(bad));
        var rel = new ChecklistTemplateItem { Title = "x", LinkUrl = "/kb/articles/1" };
        Assert.NotNull(ChecklistTemplateValidator.ValidateItem(rel));
        var ok = new ChecklistTemplateItem { Title = "x", LinkUrl = " https://datawolk.be/manual " };
        Assert.Null(ChecklistTemplateValidator.ValidateItem(ok));
        Assert.Equal("https://datawolk.be/manual", ok.LinkUrl);
    }

    [Fact]
    public void Definition_round_trips_through_json()
    {
        var def = Def("Step A", "Step B");
        def.Sections[0].Items[1].IsRequired = false;
        def.Sections[0].Items[1].TeamLabel = "Back Office";
        var json = def.ToJson();
        var back = ChecklistTemplateDefinition.Parse(json);
        Assert.Equal(2, back.ItemCount);
        Assert.False(back.Sections[0].Items[1].IsRequired);
        Assert.Equal("Back Office", back.Sections[0].Items[1].TeamLabel);
        Assert.Contains("Step A", back.FlattenForSearch());
        Assert.Empty(ChecklistTemplateDefinition.Parse("not json").Sections);
    }

    [Fact]
    public async Task Item_search_source_is_hidden_from_customers_and_agents_without_queues()
    {
        var src = new TicketChecklistItemSearchSource(null!);
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Customer", null)));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", new[] { Guid.NewGuid() })));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));

        // null! data source proves these branches never open a connection.
        var customer = await src.SearchAsync(new SearchRequest("prospect", null, 10, 0), new SearchPrincipal(Guid.NewGuid(), "Customer", null), default);
        Assert.Empty(customer.Hits);
        var noQueues = await src.SearchAsync(new SearchRequest("prospect", null, 10, 0), new SearchPrincipal(Guid.NewGuid(), "Agent", Array.Empty<Guid>()), default);
        Assert.Empty(noQueues.Hits);
        Assert.Equal(SearchSourceKind.ChecklistItems, customer.Kind);
    }

    [Fact]
    public async Task Template_search_source_is_admin_only()
    {
        var src = new ChecklistTemplateSearchSource(null!);
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Agent", new[] { Guid.NewGuid() })));
        Assert.False(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Customer", null)));
        Assert.True(src.IsAvailableFor(new SearchPrincipal(Guid.NewGuid(), "Admin", null)));
        var agent = await src.SearchAsync(new SearchRequest("onboarding", null, 10, 0), new SearchPrincipal(Guid.NewGuid(), "Agent", new[] { Guid.NewGuid() }), default);
        Assert.Empty(agent.Hits);
        Assert.Equal(0, agent.TotalInGroup);
        Assert.Equal("checklist-templates", SearchSourceKind.ChecklistTemplates);
        Assert.Equal("checklist-items", SearchSourceKind.ChecklistItems);
    }
}
