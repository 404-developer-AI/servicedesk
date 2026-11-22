using Dapper;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for the admin-only configuration sources: checklist
/// templates, intake templates, surveys, triggers, signatures and IP rules.
/// Admin finds them; agents (even holding every feature flag) and customers
/// get zero hits.
[Collection(PostgresCollection.Name)]
public sealed class AdminSearchSourcesDbTests
{
    private readonly PostgresDatabase _db;

    public AdminSearchSourcesDbTests(PostgresDatabase db) => _db = db;

    private static SearchPrincipal FlaggedAgent() => Agent(
        SearchFeature.Contracts, SearchFeature.AdsolutOrders, SearchFeature.Assets,
        SearchFeature.AdsolutTimesheet, SearchFeature.Feedback, SearchFeature.BackofficeForms);

    private async Task AssertAdminOnlyAsync(ISearchSource source, string token, params string[] expectedIds)
    {
        Assert.Equal(expectedIds.Order().ToArray(), Ids(await source.SearchAsync(Query(token), Admin(), default)));
        await AssertNoHitsAsync(source, Agent(), token);
        await AssertNoHitsAsync(source, FlaggedAgent(), token);
        await AssertNoHitsAsync(source, Customer(), token);
    }

    [PostgresFact]
    public async Task Checklist_templates_are_admin_only()
    {
        var token = Token();
        Guid id;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            id = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO checklist_templates (name, search_text) VALUES (@name, @name) RETURNING id",
                new { name = $"{token} onboarding" });

        await AssertAdminOnlyAsync(new ChecklistTemplateSearchSource(_db.DataSource), token, id.ToString());
    }

    [PostgresFact]
    public async Task Intake_templates_are_admin_only_and_inactive_ones_are_hidden()
    {
        var token = Token();
        Guid active;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            active = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO intake_templates (name) VALUES (@name) RETURNING id",
                new { name = $"{token} new starter" });
            await c.ExecuteAsync(
                "INSERT INTO intake_templates (name, is_active) VALUES (@name, FALSE)",
                new { name = $"{token} retired" });
        }

        await AssertAdminOnlyAsync(new IntakeTemplateSearchSource(_db.DataSource), token, active.ToString());
    }

    [PostgresFact]
    public async Task Surveys_are_admin_only_and_inactive_ones_are_hidden()
    {
        var token = Token();
        Guid active;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            active = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO surveys (name) VALUES (@name) RETURNING id",
                new { name = $"{token} satisfaction" });
            await c.ExecuteAsync(
                "INSERT INTO surveys (name, is_active) VALUES (@name, FALSE)",
                new { name = $"{token} retired" });
        }

        await AssertAdminOnlyAsync(new SurveySearchSource(_db.DataSource), token, active.ToString());
    }

    [PostgresFact]
    public async Task Triggers_are_admin_only()
    {
        var token = Token();
        Guid id;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            id = await c.ExecuteScalarAsync<Guid>("""
                INSERT INTO triggers (name, activator_kind, activator_mode)
                VALUES (@name, 'action', 'selective')
                RETURNING id
                """,
                new { name = $"{token} escalate" });

        await AssertAdminOnlyAsync(new TriggerSearchSource(_db.DataSource), token, id.ToString());
    }

    [PostgresFact]
    public async Task Signatures_are_admin_only()
    {
        var token = Token();
        Guid id;
        await using (var c = await _db.DataSource.OpenConnectionAsync())
            id = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO mail_signatures (name) VALUES (@name) RETURNING id",
                new { name = $"{token} support" });

        await AssertAdminOnlyAsync(new SignatureSearchSource(_db.DataSource), token, id.ToString());
    }

    [PostgresFact]
    public async Task Ip_rules_are_admin_only_and_only_open_proposals_are_listed()
    {
        const string ruleIp = "192.0.2.213";
        const string openProposalIp = "198.51.100.211";
        const string dismissedProposalIp = "198.51.100.212";
        var token = Token();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("DELETE FROM security_ip_rules WHERE ip = @ruleIp", new { ruleIp });
            await c.ExecuteAsync(
                "DELETE FROM security_ip_proposals WHERE ip = ANY(@ips)",
                new { ips = new[] { openProposalIp, dismissedProposalIp } });
            await c.ExecuteAsync("""
                INSERT INTO security_ip_rules (ip, kind, source, reason, created_by)
                VALUES (@ruleIp, 'block', 'admin', @reason, 'test')
                """,
                new { ruleIp, reason = $"{token} scanner" });
            await c.ExecuteAsync("""
                INSERT INTO security_ip_proposals (ip, status, reasons, first_seen_utc, last_seen_utc)
                VALUES (@openProposalIp, 'open', ARRAY['scanner'], now(), now()),
                       (@dismissedProposalIp, 'dismissed', ARRAY['scanner'], now(), now())
                """,
                new { openProposalIp, dismissedProposalIp });
        }

        var source = new IpRuleSearchSource(_db.DataSource);

        Assert.Equal(new[] { ruleIp }, Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(new[] { openProposalIp }, Ids(await source.SearchAsync(Query(openProposalIp), Admin(), default)));
        Assert.Empty((await source.SearchAsync(Query(dismissedProposalIp), Admin(), default)).Hits);

        foreach (var q in new[] { token, openProposalIp, ruleIp })
        {
            await AssertNoHitsAsync(source, Agent(), q);
            await AssertNoHitsAsync(source, FlaggedAgent(), q);
            await AssertNoHitsAsync(source, Customer(), q);
        }
    }
}
