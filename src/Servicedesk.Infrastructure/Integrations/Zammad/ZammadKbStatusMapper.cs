namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Pure helper that maps Zammad's three nullable status timestamps onto
/// our explicit enum used by <c>kb_articles.status</c>. Direct mapping
/// per the v0.0.43 scope decision:
/// <list type="bullet">
/// <item><c>archived_at</c> set → "Archived"</item>
/// <item>else <c>published_at</c> set → "Published"</item>
/// <item>else <c>internal_at</c> set → "Internal"</item>
/// <item>else → "Draft"</item>
/// </list>
/// Precedence is highest-bucket-wins because Zammad keeps the older
/// timestamps populated after a status flip — e.g. an archived article
/// still carries its original published_at. The importer treats that as
/// "currently Archived" rather than "Published".
public static class ZammadKbStatusMapper
{
    public const string Draft = "Draft";
    public const string Internal = "Internal";
    public const string Published = "Published";
    public const string Archived = "Archived";

    public static string Map(
        DateTimeOffset? internalAt,
        DateTimeOffset? publishedAt,
        DateTimeOffset? archivedAt)
    {
        if (archivedAt is not null) return Archived;
        if (publishedAt is not null) return Published;
        if (internalAt is not null) return Internal;
        return Draft;
    }
}
