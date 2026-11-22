using Dapper;
using Servicedesk.Infrastructure.Search;
using Xunit;
using static Servicedesk.Api.Tests.Database.SearchTestKit;

namespace Servicedesk.Api.Tests.Database;

/// Real-SQL authorization for the knowledge base: agents and admins find
/// Internal and Published articles, Draft and Archived ones stay hidden, and
/// customers never get hits.
[Collection(PostgresCollection.Name)]
public sealed class KbArticleSearchSourceDbTests
{
    private readonly PostgresDatabase _db;

    public KbArticleSearchSourceDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Only_internal_and_published_articles_are_found_and_customers_get_nothing()
    {
        var token = Token();
        var ids = new Dictionary<string, Guid>();
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            var locale = await c.ExecuteScalarAsync<string?>("SELECT default_locale_code FROM knowledge_base LIMIT 1");
            Assert.NotNull(locale);
            var sectionId = await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO kb_sections (slug) VALUES (@slug) RETURNING id",
                new { slug = $"s-{Guid.NewGuid():N}" });
            foreach (var status in new[] { "Draft", "Internal", "Published", "Archived" })
            {
                var articleId = await c.ExecuteScalarAsync<Guid>("""
                    INSERT INTO kb_articles (section_id, slug, status)
                    VALUES (@sectionId, @slug, @status)
                    RETURNING id
                    """,
                    new { sectionId, slug = $"a-{Guid.NewGuid():N}", status });
                await c.ExecuteAsync("""
                    INSERT INTO kb_article_translations (article_id, locale_code, title, body_text)
                    VALUES (@articleId, @locale, @title, 'Example body')
                    """,
                    new { articleId, locale, title = $"{token} {status}" });
                ids[status] = articleId;
            }
        }

        var source = new KbArticleSearchSource(_db.DataSource);
        var expected = Sorted(ids["Internal"], ids["Published"]);

        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Admin(), default)));
        Assert.Equal(expected, Ids(await source.SearchAsync(Query(token), Agent(), default)));

        await AssertNoHitsAsync(source, Customer(), token);
    }
}
