using Servicedesk.Infrastructure.KnowledgeBase;
using Xunit;

namespace Servicedesk.Api.Tests;

public class KbSlugGeneratorTests
{
    [Theory]
    [InlineData("Hello world", "hello-world")]
    [InlineData("How to reset your password", "how-to-reset-your-password")]
    [InlineData("Multiple   spaces", "multiple-spaces")]
    [InlineData("Trim --- hyphens---", "trim-hyphens")]
    [InlineData("CAPS to lowercase", "caps-to-lowercase")]
    public void Basic_titles(string title, string expected)
    {
        Assert.Equal(expected, KbSlugGenerator.Slugify(title));
    }

    [Theory]
    [InlineData("café", "cafe")]
    [InlineData("naïve", "naive")]
    [InlineData("piñata", "pinata")]
    [InlineData("België", "belgie")]
    public void Diacritics_are_stripped(string title, string expected)
    {
        Assert.Equal(expected, KbSlugGenerator.Slugify(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("🎉🎉🎉")]
    [InlineData("中文")]
    public void Empty_or_unmappable_falls_back(string? title)
    {
        Assert.Equal(KbSlugGenerator.Fallback, KbSlugGenerator.Slugify(title));
    }

    [Fact]
    public void Truncates_to_max_length_at_word_boundary()
    {
        var input = new string('a', 200);
        var slug = KbSlugGenerator.Slugify(input);
        Assert.True(slug.Length <= KbSlugGenerator.MaxLength);
    }

    [Fact]
    public void Trailing_hyphen_after_truncation_is_removed()
    {
        // 79 a's + "-" + 5 b's. The slug builder should not leave a trailing hyphen.
        var input = new string('a', 79) + " " + new string('b', 5);
        var slug = KbSlugGenerator.Slugify(input);
        Assert.False(slug.EndsWith("-"), $"slug should not end with a hyphen: '{slug}'");
        Assert.True(slug.Length <= KbSlugGenerator.MaxLength);
    }

    [Theory]
    [InlineData("Hello, world!", "hello-world")]
    [InlineData("foo/bar/baz", "foo-bar-baz")]
    [InlineData("price: $9.99", "price-9-99")]
    [InlineData("under_score", "under-score")]
    public void Special_chars_collapse_to_hyphens(string title, string expected)
    {
        Assert.Equal(expected, KbSlugGenerator.Slugify(title));
    }

    [Fact]
    public void Output_matches_database_check_constraint()
    {
        // The DB CHECK is `^[a-z0-9]+(-[a-z0-9]+)*$`. Any non-empty slug we
        // emit must satisfy that constraint or the next INSERT 23514's.
        var samples = new[]
        {
            "Hello world",
            "café & bistro",
            "What's new?",
            "Two   spaces, one—em-dash",
            "naïve approach",
        };
        foreach (var s in samples)
        {
            var slug = KbSlugGenerator.Slugify(s);
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", slug);
        }
    }
}
