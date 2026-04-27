namespace Servicedesk.Infrastructure.Triggers;

/// Public-facing catalog of every condition field the matcher resolves —
/// surfaced through the admin metadata endpoint so the editor can build
/// pickers without re-encoding the keys client-side. Each entry pairs the
/// canonical key (e.g. <c>ticket.queue.id</c>) with a human label and a
/// type hint so the UI knows whether to render a taxonomy picker, a text
/// input, or a sender enum.
public static class TriggerConditionFieldCatalog
{
    public static readonly IReadOnlyList<TriggerConditionField> All = new TriggerConditionField[]
    {
        new(TriggerFieldKeys.TicketQueueId,     "Ticket queue",       "queue"),
        new(TriggerFieldKeys.TicketStatusId,    "Ticket status",      "status"),
        new(TriggerFieldKeys.TicketPriorityId,  "Ticket priority",    "priority"),
        new(TriggerFieldKeys.TicketCategoryId,  "Ticket category",    "category"),
        new(TriggerFieldKeys.TicketOwnerId,     "Ticket owner",       "agent"),
        new(TriggerFieldKeys.TicketRequesterId, "Ticket requester",   "contact"),
        new(TriggerFieldKeys.TicketCompanyId,   "Ticket company",     "company"),
        new(TriggerFieldKeys.TicketSubject,     "Ticket subject",     "string"),
        new(TriggerFieldKeys.TicketTags,        "Ticket tags",        "tags"),
        new(TriggerFieldKeys.ArticleSender,     "Article sender",     "sender"),
        new(TriggerFieldKeys.ArticleType,       "Article type",       "article-type"),
        new(TriggerFieldKeys.ArticleBodyText,   "Article body",       "string"),
        new(TriggerFieldKeys.ArticleHasAttachments, "Article has attachments", "boolean"),
    };
}

/// One entry in the condition-field catalog. <see cref="Type"/> is a
/// hint string the UI uses to pick a renderer (e.g. <c>"queue"</c> →
/// queue dropdown, <c>"string"</c> → text input).
public sealed record TriggerConditionField(string Key, string Label, string Type);
