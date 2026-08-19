using Servicedesk.Infrastructure.Mail.Outbound;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the guards on the recipient-suggestion SQL that backs the mail
/// composer's To/Cc/Bcc typeahead (see RecipientSuggestionRepository).
/// The query needs a real Postgres to exercise, so these tests assert the
/// generated SQL — the guards are the security/behaviour contract:
/// frequency counts outbound mail only, suggested contacts are active-only,
/// and the company-ranked block always sorts above the general matches.
public sealed class RecipientSuggestionSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Usage_counts_outbound_mail_of_the_company_only(bool hasSearch)
    {
        var sql = RecipientSuggestionRepository.BuildSuggestionsSql(hasSearch);

        Assert.Contains("m.direction = 'Outbound'", sql);
        Assert.Contains("t.company_id = @companyId", sql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Company_contacts_are_active_only(bool hasSearch)
    {
        var sql = RecipientSuggestionRepository.BuildSuggestionsSql(hasSearch);

        Assert.Contains("cc.company_id = @companyId AND c.is_active = TRUE", sql);
    }

    [Fact]
    public void General_search_block_is_active_only_and_only_present_with_a_search_term()
    {
        // Collapse whitespace so the assertion doesn't depend on raw-string
        // indentation.
        var withSearch = string.Join(" ",
            RecipientSuggestionRepository.BuildSuggestionsSql(hasSearch: true)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var withoutSearch = RecipientSuggestionRepository.BuildSuggestionsSql(hasSearch: false);

        Assert.Contains("WHERE c.is_active = TRUE AND (c.email ILIKE @search", withSearch);
        Assert.DoesNotContain("@search", withoutSearch);
    }

    [Fact]
    public void Company_block_sorts_above_general_matches()
    {
        // block 0 = company-scoped, block 1 = general; the outer ORDER BY
        // must lead with it so ranking-by-usage never mixes the two.
        var sql = RecipientSuggestionRepository.BuildSuggestionsSql(hasSearch: true);

        Assert.Contains("0 AS block", sql);
        Assert.Contains("1 AS block", sql);
        Assert.Contains("ORDER BY s.block, s.usage_count DESC", sql);
    }

    [Fact]
    public void Result_is_capped()
    {
        var sql = RecipientSuggestionRepository.BuildSuggestionsSql(hasSearch: true);

        Assert.Contains("LIMIT @limit", sql);
    }
}
