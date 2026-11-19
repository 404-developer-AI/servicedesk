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
    /// True when the article that was just added is the ticket-CREATING event
    /// (e.g. the inbound customer mail that opened the ticket), as opposed to a
    /// reply appended to an existing ticket. Drives the
    /// <see cref="TriggerFieldKeys.TicketIsNew"/> condition so an auto-reply can
    /// fire only on the first message. Defaults false — only the mail-ingest
    /// creation path sets it.
    public bool IsTicketCreation { get; init; }

    /// Set only on a form pass: a back-office step (v0.1.54 — a form was
    /// submitted to, handled by or reopened on the Back office forms list) or
    /// a form save (v0.1.58 — <see cref="TriggerFormEvent.SaveEvent"/> set).
    /// Form triggers (those referencing a <c>form.*</c> field) run only on
    /// such a pass, and such a pass runs only form triggers — see
    /// <see cref="TriggerFieldKeys.IsFormField"/>.
    public TriggerFormEvent? FormEvent { get; init; }

    /// Form triggers run only on form passes and form passes run only form
    /// triggers. Among form triggers, those referencing
    /// <see cref="TriggerFieldKeys.FormSaveEvent"/> run only on save passes,
    /// every other form trigger only on back-office passes — so a trigger
    /// that only names a template never fires twice for one save.
    /// <paramref name="referencedFields"/> = the trigger's condition fields.
    public bool RunsTriggerReferencing(IReadOnlySet<string> referencedFields)
    {
        if (!referencedFields.Any(TriggerFieldKeys.IsFormField)) return FormEvent is null;
        if (FormEvent is null) return false;
        return referencedFields.Contains(TriggerFieldKeys.FormSaveEvent) == FormEvent.IsSavePass;
    }

    public static TriggerChangeSet ForForm(TriggerFormEvent evt)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            evt.IsSavePass ? TriggerFieldKeys.FormSaveEvent : TriggerFieldKeys.FormBackofficeEvent,
            TriggerFieldKeys.FormTemplateId,
        };
        foreach (var key in evt.ChangedKeys) changed.Add(TriggerFieldKeys.FormField(key));
        return new(changed, ArticleAdded: false) { FormEvent = evt };
    }

    public static readonly TriggerChangeSet Empty = new(new HashSet<string>(), false);

    public static TriggerChangeSet AllFieldsNew()
        => new(AllTicketFieldKeys, ArticleAdded: true);

    public static TriggerChangeSet ArticleOnly(bool isTicketCreation = false)
        => new(new HashSet<string>(), ArticleAdded: true) { IsTicketCreation = isTicketCreation };

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
        TriggerFieldKeys.TicketIsCallback,
        TriggerFieldKeys.TicketIsResearch,
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

    public const string TicketIsNew = "ticket.is_new";

    /// v0.1.17 — the agent-set Call-back / Research ticket flags (columns
    /// tickets.is_callback / is_research, toggled through the PATCH path).
    public const string TicketIsCallback = "ticket.is_callback";
    public const string TicketIsResearch = "ticket.is_research";

    /// True when the ticket references at least one Adsolut order — i.e. an
    /// order pill (<c>data-order-id</c>) was inserted via the <c>::</c> picker
    /// in the ticket body or any note/mail (see
    /// <c>IAdsolutOrderRepository.HasLinkedOrderAsync</c>). Resolved off
    /// <see cref="TriggerEvaluationContext.HasLinkedOrder"/>, which each
    /// matching site pre-loads only when a trigger actually references this
    /// key — see the <c>ReferencedFields</c> pre-scan in StatusGateService /
    /// TriggerService / FirstOpenGateService. Not a ticket column, so it is
    /// deliberately absent from <see cref="TriggerChangeSet.AllTicketFieldKeys"/>:
    /// tagging an order is a content edit, not a ticket-field mutation, and
    /// does not flow through the PATCH change-set.
    public const string TicketHasLinkedOrder = "ticket.has_linked_order";

    public const string ArticleSender = "article.sender";
    public const string ArticleType = "article.type";
    public const string ArticleBodyText = "article.body_text";
    public const string ArticleIsInternal = "article.is_internal";
    public const string ArticleHasAttachments = "article.has_attachments";

    /// v0.1.54 — back-office form events: <c>submitted</c> (a form of a
    /// template marked "Send to back office" was filled in), <c>handled</c>,
    /// <c>reopened</c> (by back office, or by an edit after handling).
    public const string FormBackofficeEvent = "form.backoffice_event";
    public const string FormTemplateId = "form.template.id";

    /// v0.1.58 — a filled form was saved: <c>created</c> or <c>updated</c>.
    public const string FormSaveEvent = "form.save_event";

    /// v0.1.58 — <c>form.field.&lt;trigger key&gt;</c>: a form field an admin
    /// gave a trigger key (<see cref="Forms.FormField.TriggerKey"/>).
    public const string FormFieldPrefix = "form.field.";

    public static string FormField(string triggerKey) => FormFieldPrefix + triggerKey;

    /// The trigger key of a <c>form.field.*</c> condition key, else null.
    public static string? FormFieldKey(string key)
        => key.StartsWith(FormFieldPrefix, StringComparison.OrdinalIgnoreCase) ? key[FormFieldPrefix.Length..] : null;

    public static bool IsFormField(string key) => key.StartsWith("form.", StringComparison.OrdinalIgnoreCase);
}

/// The form step a form pass is about: a back-office step (<see cref="Event"/>)
/// or, when <see cref="SaveEvent"/> is set, a form save. <see cref="AnchorEventId"/>
/// is the form's latest note on the ticket — what a notify_agent bell points at.
public sealed record TriggerFormEvent(
    string Event,
    Guid FormId,
    Guid? TemplateId,
    string FormName,
    Guid? SubmittedByUserId,
    string? SubmittedByName,
    Guid? ActorUserId,
    string? HandledByName,
    string HandledNote,
    long? AnchorEventId)
{
    /// v0.1.58 — <c>created</c> / <c>updated</c> on a save pass; null on a
    /// back-office pass.
    public string? SaveEvent { get; init; }

    public bool IsSavePass => SaveEvent is not null;

    /// The form's fields that have a trigger key, by key — empty ones
    /// included (so an action can tell "empty" from "not on this form").
    public IReadOnlyDictionary<string, TriggerFormField> Fields { get; init; }
        = new Dictionary<string, TriggerFormField>(StringComparer.OrdinalIgnoreCase);

    /// Keys whose value this save changed (every filled key on create; empty
    /// on a back-office pass). Feeds <c>has_changed</c> on <c>form.field.*</c>.
    public IReadOnlySet<string> ChangedKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// One keyed form field as a trigger sees it. <see cref="RefId"/> = the agent /
/// contact / queue id of a reference field; <see cref="DateTimeUtc"/> = the
/// moment of a DateTime field; <see cref="Display"/> = the value as text (what
/// <c>#{form.field.&lt;key&gt;}</c> renders, and what text conditions compare).
public sealed record TriggerFormField(string Type, string Label, string Display, Guid? RefId, DateTime? DateTimeUtc)
{
    public bool IsEmpty => Display.Length == 0 && RefId is null && DateTimeUtc is null;

    /// What the condition matcher compares: the id for references, else the text.
    public string? ConditionValue => IsEmpty ? null : RefId?.ToString() ?? Display;
}
