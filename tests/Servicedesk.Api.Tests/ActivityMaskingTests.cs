using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Activity;
using Servicedesk.Infrastructure.Dashboard;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the queue-level authorization that masks ticket references in the
/// dashboard agent-activity tile and the activity feed. The leak this
/// guards against: an agent without access to a queue could read the
/// subject + number of tickets in that queue through the live feeds, even
/// though the ticket-detail endpoint correctly refuses them. After
/// masking, such a ticket becomes a content-free "restricted" placeholder.
public sealed class ActivityMaskingTests
{
    private static readonly Guid QueueAllowed = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid QueueDenied = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static QueueAccessScope Scope(params Guid[] queues)
        => new(isAdmin: false, queues.ToHashSet());

    private static AgentActivityTicket Ticket(Guid queueId) => new(
        Id: Guid.NewGuid(), Number: 4242, Subject: "Payroll export broken",
        StatusName: "Open", StatusColor: "#abcdef", StatusStateCategory: "open",
        Restricted: false, QueueId: queueId);

    // ---- AgentActivity (dashboard tile) ----

    [Fact]
    public void Agent_activity_masks_ticket_in_inaccessible_queue()
    {
        var denied = Ticket(QueueDenied);
        var activity = new AgentActivity(
            UserId: Guid.NewGuid(), Email: "peer@x.test", RoleName: "Agent",
            Online: true, CallState: "idle",
            Viewing: denied, Recent: new[] { Ticket(QueueAllowed) });

        var masked = AgentActivityMasking.ForViewer(activity, Scope(QueueAllowed));

        // The viewed ticket sits in a denied queue → fully masked.
        Assert.NotNull(masked.Viewing);
        Assert.True(masked.Viewing!.Restricted);
        Assert.Equal(0, masked.Viewing.Number);
        Assert.Equal("", masked.Viewing.Subject);
        Assert.Equal("", masked.Viewing.StatusName);
        Assert.Equal(denied.Id, masked.Viewing.Id); // id kept for keying
        Assert.Equal(default, masked.Viewing.QueueId);

        // The recent ticket is in an accessible queue → untouched.
        Assert.False(masked.Recent[0].Restricted);
        Assert.Equal(4242, masked.Recent[0].Number);
        Assert.Equal("Payroll export broken", masked.Recent[0].Subject);
    }

    [Fact]
    public void Agent_activity_admin_sees_everything()
    {
        var activity = new AgentActivity(
            UserId: Guid.NewGuid(), Email: "peer@x.test", RoleName: "Agent",
            Online: true, CallState: "idle",
            Viewing: Ticket(QueueDenied), Recent: Array.Empty<AgentActivityTicket>());

        var masked = AgentActivityMasking.ForViewer(activity, QueueAccessScope.AdminScope);

        Assert.Same(activity, masked); // unchanged reference on the hot path
        Assert.False(masked.Viewing!.Restricted);
        Assert.Equal("Payroll export broken", masked.Viewing.Subject);
    }

    [Fact]
    public void Agent_activity_with_no_queue_access_masks_all()
    {
        var activity = new AgentActivity(
            UserId: Guid.NewGuid(), Email: "peer@x.test", RoleName: "Agent",
            Online: true, CallState: "idle",
            Viewing: Ticket(QueueAllowed), Recent: new[] { Ticket(QueueDenied) });

        var masked = AgentActivityMasking.ForViewer(activity, Scope()); // empty set

        Assert.True(masked.Viewing!.Restricted);
        Assert.True(masked.Recent[0].Restricted);
    }

    // ---- ActivityFeedEntry (activity feed live broadcast) ----

    private static ActivityFeedEntry FeedEntry(Guid? queueId) => new()
    {
        Id = 7,
        AgentEmail = "peer@x.test",
        EventType = "ticket_note_internal",
        EntityType = "ticket",
        EntityId = "33333333-3333-3333-3333-333333333333",
        Summary = "added an internal note on",
        TicketNumber = 4242,
        TicketSubject = "Payroll export broken",
        TicketQueueId = queueId,
    };

    [Fact]
    public void Feed_entry_masks_ticket_in_inaccessible_queue()
    {
        var masked = ActivityFeedMasking.ForViewer(FeedEntry(QueueDenied), Scope(QueueAllowed));

        Assert.True(masked.TicketRestricted);
        Assert.Null(masked.TicketNumber);
        Assert.Null(masked.TicketSubject);
        Assert.Null(masked.EntityId); // stripped so the client cannot deep-link
        Assert.Null(masked.TicketQueueId);
        Assert.Equal("added an internal note on", masked.Summary); // row stays
    }

    [Fact]
    public void Feed_entry_visible_when_viewer_has_access()
    {
        var masked = ActivityFeedMasking.ForViewer(FeedEntry(QueueAllowed), Scope(QueueAllowed));

        Assert.False(masked.TicketRestricted);
        Assert.Equal(4242, masked.TicketNumber);
        Assert.Equal("Payroll export broken", masked.TicketSubject);
        Assert.Equal("33333333-3333-3333-3333-333333333333", masked.EntityId);
    }

    [Fact]
    public void Feed_entry_non_ticket_is_untouched()
    {
        // A login / settings event carries no ticket queue → nothing to mask.
        var entry = new ActivityFeedEntry
        {
            Id = 8, AgentEmail = "peer@x.test", EventType = "login_success",
            Summary = "signed in", TicketQueueId = null,
        };

        var masked = ActivityFeedMasking.ForViewer(entry, Scope()); // no access at all

        Assert.False(masked.TicketRestricted);
        Assert.Same(entry, masked);
    }

    [Fact]
    public void Feed_entry_does_not_mutate_shared_raw_across_recipients()
    {
        // The broadcaster reuses one raw entry for every recipient. An
        // allowed recipient must not strip state that a later denied
        // recipient still needs to detect the ticket and mask it.
        var raw = FeedEntry(QueueDenied);

        var allowed = ActivityFeedMasking.ForViewer(raw, Scope(QueueDenied));
        Assert.False(allowed.TicketRestricted);

        var denied = ActivityFeedMasking.ForViewer(raw, Scope(QueueAllowed));
        Assert.True(denied.TicketRestricted);
        Assert.Null(denied.TicketSubject);
    }
}
