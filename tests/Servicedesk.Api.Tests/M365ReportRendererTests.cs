using Servicedesk.Domain.Reports;
using Servicedesk.Infrastructure.Contracts.Reports;
using Servicedesk.Infrastructure.Integrations.M365;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pure-function coverage for the report column normaliser and the overview
/// renderer: column validation, scope filtering, summary counts, and — the
/// security-relevant one — HTML-encoding of untrusted directory data embedded
/// into the email HTML.
public sealed class M365ReportRendererTests
{
    [Fact]
    public void Normalize_drops_unknown_dedupes_and_orders_canonically()
    {
        var result = ReportColumns.Normalize(new[] { "exchange", "type", "bogus", "type", "spam" });

        // Canonical catalogue order is type, …, spam, …, exchange.
        Assert.Equal(new[] { "type", "spam", "exchange" }, result);
    }

    [Fact]
    public void Normalize_empty_falls_back_to_default()
    {
        Assert.Equal(ReportColumns.Default, ReportColumns.Normalize(Array.Empty<string>()));
        Assert.Equal(ReportColumns.Default, ReportColumns.Normalize(null));
    }

    [Fact]
    public void Render_unprotected_scope_keeps_only_action_needed_rows()
    {
        var view = new M365CompanyMailboxView
        {
            CompanyId = Guid.NewGuid(),
            SpamFilterAvailable = true,
            VeeamAvailable = true,
            Mailboxes = new[]
            {
                Mb("Protected One", spam: true, ex: true, od: true),
                Mb("Needs Spam", spam: false, ex: true, od: true),
                Mb("Needs Backup", spam: true, ex: false, od: true),
            },
        };

        var result = M365ReportRenderer.Render(view, new[] { "name", "spam" }, ReportScope.Unprotected);

        Assert.Equal(2, result.MailboxCount);
        Assert.DoesNotContain(result.Rows, r => r.DisplayName == "Protected One");
    }

    [Fact]
    public void Render_summary_counts_reflect_protection()
    {
        var view = new M365CompanyMailboxView
        {
            CompanyId = Guid.NewGuid(),
            SpamFilterAvailable = true,
            VeeamAvailable = false,
            Mailboxes = new[]
            {
                Mb("A", spam: true, ex: null, od: null),
                Mb("B", spam: false, ex: null, od: null),
            },
        };

        var result = M365ReportRenderer.Render(view, new[] { "name", "spam" }, ReportScope.All);

        Assert.Equal(2, result.SpamTotal);
        Assert.Equal(1, result.SpamProtected);
        // Veeam off → backup axes report null, not a misleading zero.
        Assert.Null(result.ExchangeTotal);
        Assert.Null(result.OneDriveTotal);
    }

    [Fact]
    public void Render_encodes_untrusted_display_name()
    {
        var view = new M365CompanyMailboxView
        {
            CompanyId = Guid.NewGuid(),
            Mailboxes = new[] { Mb("<script>alert(1)</script>", spam: null, ex: null, od: null) },
        };

        var result = M365ReportRenderer.Render(view, new[] { "name" }, ReportScope.All);

        Assert.DoesNotContain("<script>", result.TableHtml);
        Assert.Contains("&lt;script&gt;", result.TableHtml);
    }

    [Fact]
    public void ApplyScalarTokens_substitutes_known_tokens()
    {
        var tokens = new Dictionary<string, string> { ["{{company.name}}"] = "Acme" };
        Assert.Equal("Hello Acme", M365ReportRenderer.ApplyScalarTokens("Hello {{company.name}}", tokens));
    }

    private static M365EnrichedMailbox Mb(string name, bool? spam, bool? ex, bool? od) => new()
    {
        ObjectId = Guid.NewGuid().ToString(),
        DisplayName = name,
        MailboxType = "UserMailbox",
        SpamFilterProtected = spam,
        ExchangeProtected = ex,
        OneDriveProtected = od,
    };
}
