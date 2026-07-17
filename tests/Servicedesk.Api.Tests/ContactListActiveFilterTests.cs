using Servicedesk.Infrastructure.Persistence.Companies;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.92 — pins the active-only guard on the contact-list SQL that backs
/// every typeahead picker (mail To/Cc/Bcc recipients, ticket requester,
/// call-popup linking, link-contact-to-company). A deactivated contact must
/// never be findable through a picker; the company detail page opts back in
/// via includeInactive so its linked-contacts card keeps showing them. The
/// query itself needs a real Postgres to exercise, so these tests assert the
/// generated SQL instead — the WHERE clause is the entire fix.
public sealed class ContactListActiveFilterTests
{
    private const string ActiveGuard = "contacts.is_active = TRUE";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Picker_sql_always_filters_to_active_contacts(bool hasCompanyFilter, bool hasSearch)
    {
        var sql = CompanyRepository.BuildListContactsSql(hasCompanyFilter, hasSearch, includeInactive: false);

        Assert.Contains(ActiveGuard, sql);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IncludeInactive_sql_does_not_filter_on_active(bool hasCompanyFilter, bool hasSearch)
    {
        var sql = CompanyRepository.BuildListContactsSql(hasCompanyFilter, hasSearch, includeInactive: true);

        Assert.DoesNotContain(ActiveGuard, sql);
    }

    [Fact]
    public void Active_guard_lands_inside_the_where_clause_before_ordering()
    {
        // The guard must be part of the WHERE chain, not appended after
        // ORDER BY (which would be invalid SQL that only surfaces at runtime).
        var sql = CompanyRepository.BuildListContactsSql(hasCompanyFilter: false, hasSearch: true, includeInactive: false);

        var guardAt = sql.IndexOf(ActiveGuard, StringComparison.Ordinal);
        var orderByAt = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
        Assert.True(guardAt >= 0 && orderByAt > guardAt,
            $"expected the active guard before ORDER BY, got: {sql}");
    }

    [Fact]
    public void Search_clause_survives_the_active_guard()
    {
        var sql = CompanyRepository.BuildListContactsSql(hasCompanyFilter: false, hasSearch: true, includeInactive: false);

        Assert.Contains("email ILIKE @search", sql);
        Assert.Contains("ORDER BY last_name, first_name LIMIT 500", sql);
    }

    [Fact]
    public void Company_filter_still_joins_contact_companies()
    {
        var sql = CompanyRepository.BuildListContactsSql(hasCompanyFilter: true, hasSearch: false, includeInactive: false);

        Assert.Contains("JOIN contact_companies cc ON cc.contact_id = contacts.id", sql);
        Assert.Contains("cc.company_id = @companyId", sql);
    }
}
