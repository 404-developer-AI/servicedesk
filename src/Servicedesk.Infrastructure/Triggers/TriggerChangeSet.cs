namespace Servicedesk.Infrastructure.Triggers;

/// What changed about a ticket during the mutation that just committed.
/// Feeds the Selective-mode short-circuit: a Selective trigger only fires
/// when at least one of its referenced condition-attributes is in
/// <see cref="ChangedFields"/>, OR when <see cref="ArticleAdded"/> is true.
///
/// Field keys use the dotted-path form the condition matcher resolves
/// (e.g. <c>ticket.priority.id</c>, <c>ticket.queue.id</c>). Always-mode
/// triggers ignore this set entirely.
public sealed record TriggerChangeSet(
    IReadOnlySet<string> ChangedFields,
    bool ArticleAdded)
{
    public static readonly TriggerChangeSet Empty = new(new HashSet<string>(), false);

    public static TriggerChangeSet AllFieldsNew()
        => new(AllTicketFieldKeys, ArticleAdded: true);

    public static TriggerChangeSet ArticleOnly()
        => new(new HashSet<string>(), ArticleAdded: true);

    /// Field-keys that map to ticket-level columns that change in
    /// <c>TicketEndpoints.update</c>. Kept in one place so call sites that
    /// build a ChangeSet from a PATCH request stay in sync with the matcher.
    public static readonly IReadOnlySet<string> AllTicketFieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TriggerFieldKeys.TicketQueueId,
        TriggerFieldKeys.TicketStatusId,
        TriggerFieldKeys.TicketPriorityId,
        TriggerFieldKeys.TicketCategoryId,
        TriggerFieldKeys.TicketOwnerId,
        TriggerFieldKeys.TicketRequesterId,
        TriggerFieldKeys.TicketCompanyId,
        TriggerFieldKeys.TicketSubject,
        TriggerFieldKeys.TicketTags,
    };
}

/// Canonical condition-field keys recognised by the matcher and by call
/// sites that build a <see cref="TriggerChangeSet"/>. Block 2 ships ticket
/// + article basics; later blocks extend this surface (working-time, custom
/// requester fields, etc.) without breaking existing trigger rows.
public static class TriggerFieldKeys
{
    public const string TicketQueueId = "ticket.queue.id";
    public const string TicketStatusId = "ticket.status.id";
    public const string TicketPriorityId = "ticket.priority.id";
    public const string TicketCategoryId = "ticket.category.id";
    public const string TicketOwnerId = "ticket.owner.id";
    public const string TicketRequesterId = "ticket.requester.id";
    public const string TicketCompanyId = "ticket.company.id";
    public const string TicketSubject = "ticket.subject";
    public const string TicketTags = "ticket.tags";

    public const string ArticleSender = "article.sender";
    public const string ArticleType = "article.type";
    public const string ArticleBodyText = "article.body_text";
    public const string ArticleHasAttachments = "article.has_attachments";
}
