using System.Text;
using Dapper;
using Servicedesk.Api.Preferences;
using Servicedesk.Api.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Search;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.18 — per-view column lock, column-search whitelist and the shared
/// prefix tsquery used by the per-view "Full" search.
public sealed class ViewColumnsAndSearchTests
{
    private const string Admin = "number,subject,updatedUtc";

    [Fact]
    public void Locked_view_ignores_both_user_levels_and_returns_view_columns()
    {
        var pref = ColumnPreferenceResolver.Resolve("subject,number", "number,statusName", false, "queueName", Admin);
        Assert.Equal("number,statusName", pref.Columns);
        Assert.Equal("view", pref.Source);
        Assert.True(pref.Locked);
    }

    [Fact]
    public void Locked_view_without_columns_falls_back_to_admin_default()
    {
        var pref = ColumnPreferenceResolver.Resolve("subject", null, false, "queueName", Admin);
        Assert.Equal(Admin, pref.Columns);
        Assert.True(pref.Locked);
    }

    [Fact]
    public void Open_view_keeps_the_users_own_layout_and_order()
    {
        var pref = ColumnPreferenceResolver.Resolve("subject,number,statusName", "number,subject", true, null, Admin);
        Assert.Equal("subject,number,statusName", pref.Columns);
        Assert.Equal("user-view", pref.Source);
        Assert.False(pref.Locked);
    }

    [Fact]
    public void Open_view_cascade_is_unchanged()
    {
        Assert.Equal("view", ColumnPreferenceResolver.Resolve(null, "number", true, "subject", Admin).Source);
        Assert.Equal("user", ColumnPreferenceResolver.Resolve(null, null, true, "subject", Admin).Source);
        Assert.Equal("default", ColumnPreferenceResolver.Resolve(null, null, true, null, Admin).Source);
    }

    [Theory]
    [InlineData("number,subject,timeLogged", true)]
    [InlineData("", true)]
    [InlineData("number,,subject", false)]
    [InlineData("number;DROP", false)]
    [InlineData("number, subject", false)]
    [InlineData(null, false)]
    public void Layout_validation(string? layout, bool valid) =>
        Assert.Equal(valid, ColumnPreferenceResolver.IsValidLayout(layout));

    [Fact]
    public void Column_search_drops_unknown_and_non_text_fields()
    {
        var fields = TicketColumnSearch.FilterFields(new[] { "subject", "t.subject); DROP TABLE x;--", "updatedUtc", "timeLogged", "subject", "companyName" });
        Assert.Equal(new[] { "subject", "companyName" }, fields);
    }

    [Fact]
    public void Column_search_binds_every_token_as_a_parameter()
    {
        var sql = new StringBuilder();
        var p = new DynamicParameters();
        TicketColumnSearch.Append(sql, p, "acme 50%_off", new[] { "subject", "companyName" });

        var text = sql.ToString();
        Assert.DoesNotContain("acme", text);
        Assert.DoesNotContain("50", text);
        Assert.Contains("@ColTok0", text);
        Assert.Contains("@ColTok1", text);
        Assert.Equal("%acme%", p.Get<string>("ColTok0"));
        Assert.Equal("%50\\%\\_off%", p.Get<string>("ColTok1"));
    }

    [Fact]
    public void Column_search_without_valid_fields_adds_nothing()
    {
        var sql = new StringBuilder();
        TicketColumnSearch.Append(sql, new DynamicParameters(), "acme", new[] { "bogus" });
        Assert.Equal(string.Empty, sql.ToString());
    }

    [Fact]
    public void Column_search_caps_token_count()
    {
        var tokens = TicketColumnSearch.Tokenize(string.Join(' ', Enumerable.Range(0, 20).Select(i => "w" + i)));
        Assert.Equal(TicketColumnSearch.MaxTokens, tokens.Count);
    }

    [Theory]
    [InlineData("Serv printer", "serv:* & printer:*")]
    [InlineData("a&b|!c:*'", "a:* & b:* & c:*")]
    [InlineData("   ", "")]
    public void Prefix_tsquery_is_always_valid_syntax(string raw, string expected) =>
        Assert.Equal(expected, TicketTsQuery.BuildPrefix(raw));

    [Theory]
    [InlineData("1042", 1042L)]
    [InlineData("#1042", 1042L)]
    [InlineData("10 42", null)]
    [InlineData("printer", null)]
    [InlineData("0", null)]
    public void Number_probe(string term, long? expected) =>
        Assert.Equal(expected, TicketEndpoints.ParseTicketNumberProbe(term));
}
