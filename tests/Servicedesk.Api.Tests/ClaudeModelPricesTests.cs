using Servicedesk.Infrastructure.Integrations.Claude;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Every AI call is costed by the model that answered. These pin the price
/// list format and — the bug that motivated it — that every shipped default
/// model actually has a price, so a default can never be costed at another
/// model's rate again.
public sealed class ClaudeModelPricesTests
{
    [Fact]
    public void Parses_lines_semicolons_comments_and_skips_garbage()
    {
        var prices = ClaudeModelPrices.Parse(
            "claude-haiku-5-5 = 10 / 50\n# a comment\nclaude-sonnet-5-5=200/1000 # trailing\nbroken line\nx = a / b\n; claude-opus-5-5 = 400 / 2000");

        Assert.Equal(3, prices.Count);
        Assert.Equal(new ClaudeModelPrice("claude-sonnet-5-5", 200, 1000), prices[1]);
        Assert.Equal("claude-opus-5-5", prices[2].Model);
    }

    [Fact]
    public void Resolve_uses_the_longest_prefix_case_insensitively()
    {
        const string raw = "claude-haiku = 1 / 1\nclaude-haiku-4-5 = 100 / 500\nclaude-haiku-5-5 = 10 / 50";

        Assert.Equal(100, ClaudeModelPrices.Resolve(raw, "claude-haiku-4-5-20251001")!.InputCentsPerMTok);
        Assert.Equal(10, ClaudeModelPrices.Resolve(raw, "CLAUDE-HAIKU-5-5")!.InputCentsPerMTok);
        Assert.Equal(1, ClaudeModelPrices.Resolve(raw, "claude-haiku-3")!.InputCentsPerMTok);
        Assert.Null(ClaudeModelPrices.Resolve(raw, "claude-opus-5-5"));
        Assert.Null(ClaudeModelPrices.Resolve(raw, ""));
    }

    [Theory]
    [InlineData(SettingKeys.Claude.Model)]
    [InlineData(SettingKeys.Claude.SummaryModel)]
    [InlineData(SettingKeys.Claude.StatusModel)]
    [InlineData(SettingKeys.Claude.KbChatModel)]
    public void Every_default_model_has_a_default_price(string modelKey)
    {
        string Default(string key) => SettingDefaults.All.Single(d => d.Key == key).Value;

        var price = ClaudeModelPrices.Resolve(Default(SettingKeys.Claude.ModelPrices), Default(modelKey));

        Assert.NotNull(price);
        Assert.True(price!.InputCentsPerMTok > 0 && price.OutputCentsPerMTok > 0);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-haiku-4-5-20251001", false)]
    public void Effort_support_per_model(string model, bool expected)
    {
        Assert.Equal(expected, ClaudeApiClient.ModelSupportsEffort(model));
    }
}
