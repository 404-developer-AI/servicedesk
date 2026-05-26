namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Result of <c>GET /api/v1/users/me</c>. Only the fields v0.0.41 actually
/// uses are pulled off the wire — the response payload is much wider but
/// the integration-page only needs a friendly "Connected as …" label.
public sealed record ZammadMe(
    long Id,
    string? Email,
    string? FirstName,
    string? LastName,
    string? Login,
    long? OrganizationId);

/// Combined result of the Test-connection action — the two GET calls the
/// page fires when an admin clicks the button. <see cref="ZammadVersion"/>
/// is nullable because some Zammad installs lock down /version even
/// though /users/me succeeded; the page renders "version unknown" rather
/// than treating the whole test as failed in that case.
public sealed record ZammadTestConnectionResult(
    ZammadMe Me,
    string? ZammadVersion,
    int LatencyMs);

/// Resolved connection-state shown on the integration tile + page. Mirrors
/// the Adsolut / Telavox enum shape so the SPA renders all three through
/// the same surface.
public enum ZammadConnectionState
{
    /// Master kill-switch is off (<c>Zammad.Enabled = false</c>). Tile
    /// shows "Disabled".
    Disabled = 0,

    /// Base URL or token is missing. Tile shows "Not configured".
    NotConfigured = 1,

    /// Both configured but no successful test-connection yet. Tile shows
    /// "Ready — run Test connection".
    Ready = 2,

    /// Configured and a recent Test-connection succeeded. Tile shows
    /// "Active".
    Active = 3,
}

/// Zammad group ("queue equivalent" in Zammad parlance). Only the fields
/// v0.0.41 phase 2 actually uses are surfaced; the wider record lives on
/// Zammad's side.
public sealed record ZammadGroup(long Id, string Name, bool Active);

/// Zammad ticket state. Carries the <c>state_type_id</c> so later phases
/// can derive an open/pending/closed/merged-as-closed bucket from it
/// without re-fetching <c>/ticket_state_types</c>.
public sealed record ZammadState(
    long Id,
    string Name,
    long? StateTypeId,
    bool Active);

/// Zammad ticket priority. Standard Zammad installs ship with three
/// (1=low, 2=normal, 3=high) but the API returns whatever the install
/// configured. v0.0.41 phase 3 mapping table consumes this directly.
public sealed record ZammadPriority(long Id, string Name, bool Active);

/// One Zammad user as returned by <c>GET /api/v1/users/{id}</c>. v0.0.41
/// phase 3 uses this to pre-fill the Create-contact dialog with the
/// upstream's first / last name when an admin clicks "Create" on a
/// no-contact record. The single-ticket GET only carries customer_id +
/// the resolved email; the rest of the human identity lives on the
/// user resource.
public sealed record ZammadUser(
    long Id,
    string? Email,
    string? FirstName,
    string? LastName,
    string? Login,
    long? OrganizationId,
    bool Active);

/// One row in the picker result list — denormalised projection of a
/// Zammad ticket + its resolved customer / group / state. Built either
/// from a bare expanded ticket-object or from a <c>{ tickets, assets }</c>
/// envelope by looking up the relations in the assets dictionary.
public sealed record ZammadTicketSearchItem(
    long Id,
    long? Number,
    string Title,
    long? CustomerId,
    string? CustomerEmail,
    string? CustomerName,
    long? GroupId,
    string? GroupName,
    long? StateId,
    string? StateName,
    int? ArticleCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

/// Paginated picker result. <see cref="Total"/> is the server-side total
/// match-count (Zammad fills it when we pass <c>with_total_count=true</c>);
/// null means the install refused to compute it.
public sealed record ZammadTicketSearchPage(
    IReadOnlyList<ZammadTicketSearchItem> Items,
    int? Total,
    int Page,
    int PerPage);

/// Full ticket as returned by <c>GET /api/v1/tickets/{id}?expand=true</c>.
/// v0.0.41 phase 3 (dry-run) uses everything except the article-list;
/// phase 4 (real import) layers article-fetching on top.
///
/// Zammad version note: on Zammad 7 the legacy <c>customer_email</c>
/// field is gone and the email is instead returned in the resolved
/// <c>customer</c> string. The parser tries <c>customer_email</c> first
/// for backward-compat and falls back to <c>customer</c> when it looks
/// like an email (contains <c>@</c>). On Zammad 6 the original layout
/// (<c>customer_email</c> + <c>customer</c> as display name) keeps
/// working. <see cref="GroupName"/> / <see cref="StateName"/> /
/// <see cref="PriorityName"/> / <see cref="OrganizationName"/> come
/// from the same expanded string-relations and are surfaced into the
/// mapping snapshot so the records page reads "Servicedesk | Datawolk"
/// next to <c>zammad_group_id=2</c> instead of an opaque id.
public sealed record ZammadTicket(
    long Id,
    long? Number,
    string Title,
    long? CustomerId,
    string? CustomerEmail,
    string? CustomerFirstName,
    string? CustomerLastName,
    long? OrganizationId,
    string? OrganizationName,
    long? GroupId,
    string? GroupName,
    long? StateId,
    string? StateName,
    long? PriorityId,
    string? PriorityName,
    int? ArticleCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? PendingTime);

/// One Zammad article (timeline entry) as returned by
/// <c>GET /api/v1/ticket_articles/by_ticket/{ticketId}</c>. v0.0.41 phase 4
/// (real import) walks the full article list per ticket to populate the
/// local <c>ticket_bodies</c> (first article) plus the timeline events
/// (subsequent articles).
///
/// <c>Sender</c> maps to Zammad's three-value enum
/// ("Customer" / "Agent" / "System") — used to decide whether to attach
/// the row as an author-contact or author-user event. <c>ContentType</c>
/// is the Zammad-side hint: "text/html" or "text/plain". When it is
/// HTML the body is the HTML source and a stripped plain-text mirror
/// can be derived for the text-only timeline view; when plain the body
/// is already plain text.
public sealed record ZammadArticle(
    long Id,
    long TicketId,
    string? Type,
    string? Sender,
    string? From,
    string? To,
    string? Subject,
    string? Body,
    string? ContentType,
    bool Internal,
    long? CreatedById,
    string? CreatedByEmail,
    DateTimeOffset? CreatedAt,
    // RFC-5322 Message-ID Zammad recorded for this article (present on
    // type=email articles; null on note/phone/web). The importer writes
    // it into mail_messages so a future customer reply threads back to
    // the imported ticket via In-Reply-To/References.
    string? MessageId,
    // Per-article attachment manifest. Bytes are fetched lazily by the
    // importer via <see cref="IZammadApiClient.FetchAttachmentBytesAsync"/>
    // (Zammad serves them at /api/v1/ticket_attachment/{ticketId}/{articleId}/{id});
    // this list only carries metadata so the planner can size + filter
    // before pulling. Empty list when the article has no attachments.
    IReadOnlyList<ZammadArticleAttachment> Attachments);

/// One Zammad article attachment as listed in the article-list response.
/// Zammad nests these under <c>attachments[]</c> per article. The byte
/// payload is fetched lazily from
/// <c>/api/v1/ticket_attachment/{ticketId}/{articleId}/{id}</c>; this row
/// only carries metadata so the importer can decide whether to pull (size
/// cap, mime, inline disposition) before the network hop.
///
/// <c>ContentId</c> is the RFC-2392 cid token referenced from the
/// article body's HTML <c>cid:</c> URLs. Present on inline images;
/// absent on regular attachments. Used by the local mail-timeline
/// enricher to rewrite the body to <c>/api/.../attachments/{id}</c> so
/// browsers render the image with the session cookie.
public sealed record ZammadArticleAttachment(
    long Id,
    string Filename,
    long SizeBytes,
    string MimeType,
    bool IsInline,
    string? ContentId);

/// Query parameters for <see cref="IZammadApiClient.SearchTicketsAsync"/>.
/// IDs are passed as raw long lists; the client composes the Zammad
/// Elasticsearch query string from them so the endpoint layer never has
/// to know the on-wire syntax.
public sealed record ZammadTicketSearchQuery(
    string? FreeText,
    IReadOnlyList<long> GroupIds,
    IReadOnlyList<long> StateIds,
    int Page,
    int PerPage);

// ---- v0.0.43 — Knowledge Base import ---------------------------------

/// One Knowledge Base header as returned by
/// <c>GET /api/v1/knowledge_bases</c>. Multi-KB installs return more
/// than one row; single-KB installs return exactly one. Active flag
/// surfaces whether the KB is published — inactive ones are still
/// importable but the picker labels them so the admin notices.
public sealed record ZammadKnowledgeBase(
    long Id,
    string Name,
    bool Active,
    string? DefaultLocale,
    int CategoryCount,
    int AnswerCount);

/// One Zammad KB category — a node in the section-tree. ParentId is
/// nullable for root categories. Position drives the original ordering
/// in the source; the proposer preserves it.
public sealed record ZammadKbCategory(
    long Id,
    long KnowledgeBaseId,
    long? ParentId,
    int Position,
    IReadOnlyList<ZammadKbCategoryTranslation> Translations);

/// Title (+ optional description) per locale for a category. Title is
/// the canonical display label in the section-proposal UI.
public sealed record ZammadKbCategoryTranslation(
    string LocaleCode,
    string Title);

/// One Zammad KB answer (the article-equivalent). Status is encoded as
/// three nullable timestamps in Zammad — the local importer derives a
/// single enum from them (see <c>ZammadKbStatusMapper</c>). Promoted →
/// our <c>is_featured</c>. Position is preserved 1:1.
///
/// Translations[] is keyed by locale; each translation references a
/// Content via ContentId. The /init bundle inlines the Content payload
/// so the importer doesn't need a per-translation fetch.
public sealed record ZammadKbAnswer(
    long Id,
    long KnowledgeBaseId,
    long CategoryId,
    int Position,
    bool Promoted,
    DateTimeOffset? InternalAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    long? CreatedById,
    string? InternalNote,
    IReadOnlyList<ZammadKbAnswerTranslation> Translations,
    IReadOnlyList<ZammadKbAnswerAttachment> Attachments);

/// One translation of an answer — title + body for a single locale.
/// BodyHtml is the upstream HTML (Zammad's editor produces HTML
/// natively); empty string when the translation exists but has no
/// content yet (Zammad lets editors create a translation stub before
/// authoring).
public sealed record ZammadKbAnswerTranslation(
    long Id,
    string LocaleCode,
    string Title,
    string BodyHtml);

/// Attachment metadata for one KB answer. Bytes are fetched lazily via
/// <see cref="IZammadApiClient.FetchKnowledgeBaseAttachmentBytesAsync"/>.
/// IsInline / ContentId follow the same conventions as ticket-side
/// attachments. PreviewUrl is the upstream HTML's reference to this
/// attachment — when present, the body-rewriter uses it to locate the
/// img/anchor that needs its src patched.
public sealed record ZammadKbAnswerAttachment(
    long Id,
    string Filename,
    long SizeBytes,
    string MimeType,
    bool IsInline,
    string? ContentId,
    string? PreviewUrl);

/// One answer fetched with <c>?include_contents={translation_id}</c>.
/// The translation body is inlined under <see cref="BodyHtml"/>; null
/// when the requested translation has no content row yet. Attachments
/// mirror the /init-bundle shape so the worker's rewriter can use the
/// same code path.
public sealed record ZammadKbAnswerDetail(
    long AnswerId,
    long? TranslationId,
    string? BodyHtml,
    IReadOnlyList<ZammadKbAnswerAttachment> Attachments);

/// Bundle returned by <c>GET /api/v1/knowledge_bases/init</c>. Multi-KB
/// installs return data for every KB; the importer filters on the
/// selected <see cref="ZammadKnowledgeBase.Id"/>.
///
/// Locales[] surfaces every locale Zammad has configured for any KB so
/// the importer can render a per-locale source picker. v0.0.43 imports
/// only the default locale per the scope decision; the multi-locale
/// importer lands in v0.1.x.
public sealed record ZammadKbInit(
    IReadOnlyList<ZammadKnowledgeBase> KnowledgeBases,
    IReadOnlyList<ZammadKbCategory> Categories,
    IReadOnlyList<ZammadKbAnswer> Answers,
    IReadOnlyList<string> Locales);

/// Thrown by <see cref="IZammadApiClient"/> implementations when a call
/// fails for any reason. Carries the upstream HTTP status (when one
/// landed) and a normalised error code so the endpoint layer can surface
/// a structured payload to the SPA.
public sealed class ZammadApiException : Exception
{
    public int? HttpStatus { get; }
    public string? UpstreamErrorCode { get; }

    public ZammadApiException(
        string message,
        int? httpStatus = null,
        string? upstreamErrorCode = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }

    public ZammadApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
