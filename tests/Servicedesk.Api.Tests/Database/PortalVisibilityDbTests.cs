using Servicedesk.Infrastructure.Portal;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The customer portal query layer is a whitelist (CLAUDE.md → Customer
/// portal). These tests run the real SQL of PortalTicketRepository against
/// the production schema: internal events, non-whitelisted event types,
/// project tickets, deleted tickets and other companies' tickets must never
/// reach a customer.
[Collection(PostgresCollection.Name)]
public sealed class PortalVisibilityDbTests
{
    private readonly PostgresDatabase _db;

    public PortalVisibilityDbTests(PostgresDatabase db) => _db = db;

    private PortalTicketRepository Repo => new(_db.DataSource);
    private DbSeed Seed => new(_db.DataSource);

    private static PortalViewer Viewer(Guid contactId, params (Guid CompanyId, string Role)[] companies) => new(
        Guid.NewGuid(), "jane@example.test", "Jane Example", "Active", contactId, "Jane", "Example",
        companies.Select((c, i) => new PortalCompanyAccess(c.CompanyId, "Example Company", c.Role, IsPrimary: i == 0)).ToList());

    [PostgresFact]
    public async Task Messages_contain_only_public_whitelisted_events()
    {
        var (q, s, p) = await Seed.TaxonomyAsync();
        var contact = await Seed.ContactAsync();
        var ticket = await Seed.TicketAsync("Printer offline", contact, q, s, p);

        var publicComment = await Seed.EventAsync(ticket, "Comment", isInternal: false);
        var publicMail = await Seed.EventAsync(ticket, "MailSent", isInternal: false);
        await Seed.EventAsync(ticket, "Note", isInternal: true, body: "internal note");
        await Seed.EventAsync(ticket, "Comment", isInternal: true, body: "internal comment");
        await Seed.EventAsync(ticket, "SystemNote", isInternal: false, body: "system note");
        await Seed.EventAsync(ticket, "AssignmentChange", isInternal: false);

        var slice = await Repo.ListMessagesAsync(ticket, beforeEventId: null, limit: 0, default);

        Assert.Equal(new[] { publicComment, publicMail }, slice.Events.Select(e => e.Id).OrderBy(x => x).ToArray());
        Assert.All(slice.Events, e => Assert.False(e.IsInternal));
    }

    [PostgresFact]
    public async Task Single_event_visibility_check_refuses_internal_and_non_whitelisted_events()
    {
        var (q, s, p) = await Seed.TaxonomyAsync();
        var ticket = await Seed.TicketAsync("Mailbox full", await Seed.ContactAsync(), q, s, p);
        var publicComment = await Seed.EventAsync(ticket, "Comment", isInternal: false);
        var internalNote = await Seed.EventAsync(ticket, "Note", isInternal: true);
        var systemNote = await Seed.EventAsync(ticket, "SystemNote", isInternal: false);

        Assert.True(await Repo.EventIsCustomerVisibleAsync(ticket, publicComment, default));
        Assert.False(await Repo.EventIsCustomerVisibleAsync(ticket, internalNote, default));
        Assert.False(await Repo.EventIsCustomerVisibleAsync(ticket, systemNote, default));
        // An event id of another ticket never passes for this one.
        var otherTicket = await Seed.TicketAsync("Other", await Seed.ContactAsync(), q, s, p);
        Assert.False(await Repo.EventIsCustomerVisibleAsync(otherTicket, publicComment, default));
    }

    [PostgresFact]
    public async Task Member_list_shows_own_tickets_of_the_active_company_only()
    {
        var (q, s, p) = await Seed.TaxonomyAsync();
        var company = await Seed.CompanyAsync();
        var otherCompany = await Seed.CompanyAsync();
        var me = await Seed.ContactAsync();
        var colleague = await Seed.ContactAsync();

        var own = await Seed.TicketAsync("Own ticket", me, q, s, p, company);
        await Seed.TicketAsync("Own project", me, q, s, p, company, isProject: true);
        await Seed.TicketAsync("Own deleted", me, q, s, p, company, isDeleted: true);
        await Seed.TicketAsync("Colleague ticket", colleague, q, s, p, company);
        await Seed.TicketAsync("Own ticket elsewhere", me, q, s, p, otherCompany);

        var page = await Repo.ListAsync(Viewer(me, (company, "Member")), company, PortalTicketFilter.All, null, 1, 50, default);

        Assert.Equal(new[] { own }, page.Items.Select(i => i.Id).ToArray());
    }

    [PostgresFact]
    public async Task Ticket_manager_sees_the_company_but_never_projects()
    {
        var (q, s, p) = await Seed.TaxonomyAsync();
        var company = await Seed.CompanyAsync();
        var manager = await Seed.ContactAsync();
        var colleague = await Seed.ContactAsync();

        var colleagueTicket = await Seed.TicketAsync("Colleague ticket", colleague, q, s, p, company);
        var project = await Seed.TicketAsync("Company project", colleague, q, s, p, company, isProject: true);

        var viewer = Viewer(manager, (company, "TicketManager"));
        var page = await Repo.ListAsync(viewer, company, PortalTicketFilter.All, null, 1, 50, default);

        Assert.Equal(new[] { colleagueTicket }, page.Items.Select(i => i.Id).ToArray());
        Assert.NotNull(await Repo.GetHeaderAsync(viewer, colleagueTicket, default));
        Assert.Null(await Repo.GetHeaderAsync(viewer, project, default));
    }

    [PostgresFact]
    public async Task Detail_of_a_ticket_at_an_unlinked_company_is_invisible_even_to_its_requester()
    {
        var (q, s, p) = await Seed.TaxonomyAsync();
        var linked = await Seed.CompanyAsync();
        var unlinked = await Seed.CompanyAsync();
        var me = await Seed.ContactAsync();
        var stranger = await Seed.ContactAsync();

        var ownElsewhere = await Seed.TicketAsync("Own at unlinked company", me, q, s, p, unlinked);
        var strangers = await Seed.TicketAsync("Stranger ticket", stranger, q, s, p, linked);
        var ownLinked = await Seed.TicketAsync("Own at linked company", me, q, s, p, linked);

        var viewer = Viewer(me, (linked, "Member"));
        Assert.Null(await Repo.GetHeaderAsync(viewer, ownElsewhere, default));
        Assert.Null(await Repo.GetHeaderAsync(viewer, strangers, default));
        Assert.NotNull(await Repo.GetHeaderAsync(viewer, ownLinked, default));
    }
}
