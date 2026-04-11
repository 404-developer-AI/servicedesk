namespace Servicedesk.Domain.Taxonomy;

/// Semantic grouping for a (customizable) ticket status. Custom names map
/// onto these buckets so SLA logic, "is the ticket open?" checks, and portal
/// filters keep working regardless of what the admin renamed things to.
public enum StateCategory
{
    New = 0,
    Open = 1,
    Pending = 2,
    Resolved = 3,
    Closed = 4,
}
