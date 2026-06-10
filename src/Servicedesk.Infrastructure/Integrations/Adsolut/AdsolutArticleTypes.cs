namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One page of the cursor-paged Articles list. The Articles list view returns
/// the full article record inline (code, multi-language name/description,
/// vatCode), so each item is a complete <see cref="AdsolutArticle"/> ready to
/// upsert — the sync never needs a per-article by-id fetch. <see cref="NextCursor"/>
/// + <see cref="HasNext"/> drive cursor pagination (pagingData).
public sealed record AdsolutArticleListPage(
    IReadOnlyList<AdsolutArticle> Items,
    string? NextCursor,
    bool HasNext);

/// One Adsolut ERP article (catalogue master record) as mirrored from the
/// Articles endpoint. <see cref="Name"/> / <see cref="Description"/> are the Nl
/// values picked from the API's multi-language Translation[] arrays;
/// <see cref="VatCode"/> / <see cref="VatRate"/> come from the inline vatCode
/// object (its code + the Nl description, e.g. "21%").
public sealed record AdsolutArticle(
    Guid Id,
    string? Code,
    string? Name,
    string? Description,
    string? VatCode,
    string? VatRate,
    bool Active,
    DateTimeOffset? AdsolutCreatedUtc,
    DateTimeOffset? AdsolutLastModified);
