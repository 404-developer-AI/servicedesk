using Servicedesk.Domain.KnowledgeBase;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the status-enum contract used by both the article repository and
/// the endpoint-side authorization gates (Agent ↔ Draft|Internal only).
/// If a future contributor adds a new status the unhandled cases break
/// compile and fail loudly here.
public sealed class KbArticleStatusTests
{
    [Theory]
    [InlineData("Draft", true)]
    [InlineData("Internal", true)]
    [InlineData("Published", true)]
    [InlineData("Archived", true)]
    [InlineData("draft", false)]
    [InlineData("PUBLISHED", false)]
    [InlineData("Deleted", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void IsValid_only_accepts_the_four_canonical_statuses(string status, bool expected)
    {
        Assert.Equal(expected, KbArticleStatus.IsValid(status));
    }

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("Internal", true)]
    [InlineData("Published", false)]
    [InlineData("Archived", false)]
    public void IsAgentReachable_lets_only_drafts_and_internal_through(string status, bool expected)
    {
        Assert.Equal(expected, KbArticleStatus.IsAgentReachable(status));
    }

    [Fact]
    public void Constants_match_the_database_check_constraint()
    {
        // CHECK (status IN ('Draft','Internal','Published','Archived'))
        // lives in DatabaseBootstrapper.cs. If anyone changes one without
        // the other these constants stop matching the DB and the next
        // INSERT fails with 23514. The exact strings are surface area.
        Assert.Equal("Draft", KbArticleStatus.Draft);
        Assert.Equal("Internal", KbArticleStatus.Internal);
        Assert.Equal("Published", KbArticleStatus.Published);
        Assert.Equal("Archived", KbArticleStatus.Archived);
    }
}
