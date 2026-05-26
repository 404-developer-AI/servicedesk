using Servicedesk.Infrastructure.Integrations.Zammad;
using Xunit;

namespace Servicedesk.Api.Tests;

public class ZammadKbSectionProposalBuilderTests
{
    [Fact]
    public void Builds_flat_tree_with_default_create_actions()
    {
        var kb = new ZammadKnowledgeBase(1, "Main KB", true, "nl-BE", 2, 0);
        var categories = new List<ZammadKbCategory>
        {
            new(10, 1, null, 0, new List<ZammadKbCategoryTranslation>
            {
                new("nl-BE", "Klantvragen"),
            }),
            new(11, 1, null, 1, new List<ZammadKbCategoryTranslation>
            {
                new("nl-BE", "Procedures"),
            }),
        };
        var counts = new Dictionary<long, int> { [10] = 3, [11] = 5 };

        var proposal = ZammadKbSectionProposalBuilder.Build(
            kb, categories, counts, existingDecisions: null, localePreference: "nl-BE");

        Assert.Equal(2, proposal.Nodes.Count);
        Assert.All(proposal.Nodes, n => Assert.Equal("create", n.Action));
        Assert.Equal("klantvragen", proposal.Nodes[0].ProposedSlug);
        Assert.Equal(8, proposal.TotalAnswerCount);
        Assert.Equal("nl-BE", proposal.DefaultLocale);
    }

    [Fact]
    public void Nests_children_in_depth_order_so_parents_come_before_children()
    {
        var kb = new ZammadKnowledgeBase(1, "KB", true, "nl-BE", 0, 0);
        var categories = new List<ZammadKbCategory>
        {
            new(20, 1, null, 0, new List<ZammadKbCategoryTranslation> { new("nl-BE", "Parent") }),
            new(21, 1, 20, 0, new List<ZammadKbCategoryTranslation> { new("nl-BE", "Child") }),
        };
        var proposal = ZammadKbSectionProposalBuilder.Build(
            kb, categories, new Dictionary<long, int>(), null, "nl-BE");

        Assert.Equal(2, proposal.Nodes.Count);
        Assert.Equal(20, proposal.Nodes[0].ZammadCategoryId);
        Assert.Equal(0, proposal.Nodes[0].Depth);
        Assert.Equal(21, proposal.Nodes[1].ZammadCategoryId);
        Assert.Equal(1, proposal.Nodes[1].Depth);
    }

    [Fact]
    public void Existing_decisions_are_preserved_so_re_runs_dont_reset_admin_choices()
    {
        var kb = new ZammadKnowledgeBase(1, "KB", true, "nl-BE", 0, 0);
        var categories = new List<ZammadKbCategory>
        {
            new(30, 1, null, 0, new List<ZammadKbCategoryTranslation> { new("nl-BE", "X") }),
        };
        var target = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var existing = new Dictionary<long, (string Action, Guid? TargetSectionId)>
        {
            [30] = ("merge", target),
        };
        var proposal = ZammadKbSectionProposalBuilder.Build(
            kb, categories, new Dictionary<long, int>(), existing, "nl-BE");
        Assert.Single(proposal.Nodes);
        Assert.Equal("merge", proposal.Nodes[0].Action);
        Assert.Equal(target, proposal.Nodes[0].TargetSectionId);
    }

    [Fact]
    public void Missing_translation_falls_back_to_category_id_label()
    {
        var kb = new ZammadKnowledgeBase(1, "KB", true, "nl-BE", 0, 0);
        var categories = new List<ZammadKbCategory>
        {
            new(40, 1, null, 0, new List<ZammadKbCategoryTranslation>()),
        };
        var proposal = ZammadKbSectionProposalBuilder.Build(
            kb, categories, new Dictionary<long, int>(), null, "nl-BE");
        Assert.Equal("Category #40", proposal.Nodes[0].ProposedTitle);
        Assert.Equal("category-40", proposal.Nodes[0].ProposedSlug);
    }
}
