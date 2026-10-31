using Servicedesk.Api.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests;

/// A PDF exported "without internal info" is meant to be handed to the
/// customer. Before v0.1.49 the filter only looked at the event type, so an
/// internal note (type Note, is_internal = true) still ended up in it.
public sealed class TicketExportInternalFilterTests
{
    [Theory]
    [InlineData("Note", true, true)]            // internal note
    [InlineData("Call", true, true)]            // internal phone call
    [InlineData("Comment", true, true)]         // internal comment (e.g. merged internal description)
    [InlineData("StatusChange", false, true)]   // system change event
    [InlineData("TicketFlagChange", false, true)]
    [InlineData("MailReceived", false, false)]  // customer mail
    [InlineData("MailSent", false, false)]
    [InlineData("Comment", false, false)]       // public reply
    public void Without_internal_info_drops_internal_and_system_events(string eventType, bool isInternal, bool excluded)
    {
        Assert.Equal(excluded, TicketExportEndpoints.ExcludeWithoutInternalInfo(eventType, isInternal));
    }
}
