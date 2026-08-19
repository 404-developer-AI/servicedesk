namespace Servicedesk.Domain.Tickets;

public enum TicketSource
{
    Web = 0,
    Mail = 1,
    Api = 2,
    System = 3,
    // v0.1.0 — created by a customer from the portal.
    Portal = 4,
}
