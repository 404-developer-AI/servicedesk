namespace Servicedesk.Domain.Tickets;

public enum TicketEventType
{
    Created,
    Comment,
    Mail,
    Note,
    StatusChange,
    AssignmentChange,
    PriorityChange,
    QueueChange,
    CategoryChange,
    SystemNote,
}
