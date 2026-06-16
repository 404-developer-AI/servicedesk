using System.Net;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;
using Servicedesk.Infrastructure.Storage;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Security gates of the anonymous /api/public/kb endpoints (v0.0.75):
/// the admin toggle must 404 everything while off, only Published articles
/// may ever be served, and attachments must refuse both cross-article
/// guesses and any article that is not Published. The agent-side
/// public-link-config endpoint stays session-gated.
public sealed class KbPublicEndpointsTests
{
    private static readonly Guid PublishedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InternalId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DraftId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ArchivedId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PublishedAttachmentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid InternalAttachmentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static HttpClient CreateClient(SecurityBaselineFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IKbArticleRepository>();
            services.AddSingleton<IKbArticleRepository>(new FakeKbArticleRepository());
            services.RemoveAll<IKbConfigRepository>();
            services.AddSingleton<IKbConfigRepository>(new FakeKbConfigRepository());
            services.RemoveAll<IAttachmentRepository>();
            services.AddSingleton<IAttachmentRepository>(new FakeAttachmentRepository());
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(new FakeBlobStore());
        })).CreateClient();

    [Fact]
    public async Task PublicArticle_Returns_404_When_Toggle_Off()
    {
        using var factory = new SecurityBaselineFactory();
        // Default of KnowledgeBase.PublicLinks.Enabled is "false" — no Set().
        var client = CreateClient(factory);

        var response = await client.GetAsync($"/api/public/kb/articles/{PublishedId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicArticle_Returns_200_For_Published_When_Enabled()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set("KnowledgeBase.PublicLinks.Enabled", "true");
        var client = CreateClient(factory);

        var response = await client.GetAsync($"/api/public/kb/articles/{PublishedId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Published article", json);
        // Inline attachment URLs must be rewritten to the public route so a
        // customer's browser (no session) can load the images.
        Assert.Contains($"/api/public/kb/articles/{PublishedId}/attachments/", json);
        Assert.DoesNotContain($"\"/api/kb/articles/{PublishedId}/attachments/", json);
    }

    [Theory]
    [InlineData("22222222-2222-2222-2222-222222222222")] // Internal
    [InlineData("33333333-3333-3333-3333-333333333333")] // Draft
    [InlineData("44444444-4444-4444-4444-444444444444")] // Archived
    [InlineData("99999999-9999-9999-9999-999999999999")] // unknown id
    public async Task PublicArticle_Returns_404_For_NonPublished_Even_When_Enabled(string articleId)
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set("KnowledgeBase.PublicLinks.Enabled", "true");
        var client = CreateClient(factory);

        var response = await client.GetAsync($"/api/public/kb/articles/{articleId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicAttachment_Serves_Published_Articles_Attachment()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set("KnowledgeBase.PublicLinks.Enabled", "true");
        var client = CreateClient(factory);

        var response = await client.GetAsync(
            $"/api/public/kb/articles/{PublishedId}/attachments/{PublishedAttachmentId}?inline=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PublicAttachment_Refuses_CrossArticle_Guess()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set("KnowledgeBase.PublicLinks.Enabled", "true");
        var client = CreateClient(factory);

        // The internal article's attachment requested via the published
        // article's id — OwnerId mismatch must 404, otherwise any internal
        // image would leak through any published article.
        var response = await client.GetAsync(
            $"/api/public/kb/articles/{PublishedId}/attachments/{InternalAttachmentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicAttachment_Refuses_NonPublished_Article()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set("KnowledgeBase.PublicLinks.Enabled", "true");
        var client = CreateClient(factory);

        // Correct owner pairing, but the owning article is Internal.
        var response = await client.GetAsync(
            $"/api/public/kb/articles/{InternalId}/attachments/{InternalAttachmentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicAttachment_Returns_404_When_Toggle_Off()
    {
        using var factory = new SecurityBaselineFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync(
            $"/api/public/kb/articles/{PublishedId}/attachments/{PublishedAttachmentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicLinkConfig_Requires_Session()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set("KnowledgeBase.PublicLinks.Enabled", "true");
        var client = CreateClient(factory);

        var response = await client.GetAsync("/api/kb/public-link-config");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Fakes — only the members the public endpoints touch ───

    private sealed class FakeKbArticleRepository : IKbArticleRepository
    {
        private static KbArticle Article(Guid id, string status) => new(
            id, Guid.NewGuid(), $"slug-{status.ToLowerInvariant()}", status,
            false, null, 0, null, null, DateTime.UtcNow, null,
            DateTime.UtcNow, DateTime.UtcNow, null, null);

        public Task<KbArticle?> GetArticleAsync(Guid id, CancellationToken ct)
        {
            KbArticle? result =
                id == PublishedId ? Article(id, KbArticleStatus.Published)
                : id == InternalId ? Article(id, KbArticleStatus.Internal)
                : id == DraftId ? Article(id, KbArticleStatus.Draft)
                : id == ArchivedId ? Article(id, KbArticleStatus.Archived)
                : null;
            return Task.FromResult(result);
        }

        public Task<KbArticleTranslation?> GetTranslationAsync(Guid articleId, string localeCode, CancellationToken ct) =>
            Task.FromResult<KbArticleTranslation?>(new KbArticleTranslation(
                Guid.NewGuid(), articleId, localeCode, "Published article",
                $"<p>Body with image</p><img src=\"/api/kb/articles/{articleId}/attachments/{PublishedAttachmentId}\">",
                "Body with image", DateTime.UtcNow, DateTime.UtcNow));

        public Task<KbArticleListPage> ListArticlesAsync(Guid? sectionId, string? status, string? search, int page, int pageSize, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbArticleWithTranslation?> GetArticleBySlugAsync(string sectionSlug, string articleSlug, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<KbFeaturedArticle>> ListFeaturedAsync(int limit, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbArticle> CreateArticleAsync(Guid sectionId, string slug, string status, string? editorNotes, int position, Guid actorUserId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbArticle?> UpdateArticleAsync(Guid id, Guid sectionId, string slug, string? editorNotes, int position, Guid actorUserId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbArticle?> FlipStatusAsync(Guid id, string newStatus, Guid actorUserId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbArticle?> SetFeaturedAsync(Guid id, bool isFeatured, Guid actorUserId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbArticle?> MoveArticleAsync(Guid id, Guid newSectionId, int newPosition, Guid actorUserId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> HardDeleteArticleAsync(Guid id, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbArticleTranslation> UpsertTranslationAsync(Guid articleId, string localeCode, string title, string bodyHtml, string bodyText, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> ArticleSlugExistsInSectionAsync(Guid sectionId, string slug, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeKbConfigRepository : IKbConfigRepository
    {
        public Task<KnowledgeBaseConfig> GetConfigAsync(CancellationToken ct) =>
            Task.FromResult(new KnowledgeBaseConfig(Guid.NewGuid(), true, "en", DateTime.UtcNow, DateTime.UtcNow));
        public Task<KnowledgeBaseConfig> UpdateConfigAsync(bool isActive, string defaultLocaleCode, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<KbLocale>> ListLocalesAsync(bool includeInactive, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbLocale?> GetLocaleAsync(string code, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<KbLocale> UpsertLocaleAsync(KbLocale locale, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> RemoveLocaleAsync(string code, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> LocaleHasTranslationsAsync(string code, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeAttachmentRepository : IAttachmentRepository
    {
        public Task<AttachmentRow?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            AttachmentRow? row =
                id == PublishedAttachmentId
                    ? new AttachmentRow(id, PublishedId, "KbArticle", "hash-published", 4, "image/png", "img.png", false, null, "Ready")
                : id == InternalAttachmentId
                    ? new AttachmentRow(id, InternalId, "KbArticle", "hash-internal", 4, "image/png", "secret.png", false, null, "Ready")
                : null;
            return Task.FromResult(row);
        }

        public Task<IReadOnlyList<AttachmentRow>> ListByMailAsync(Guid mailId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AttachmentRow>> ListByEventAsync(long eventId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> MarkReadyAsync(Guid attachmentId, string contentHash, long sizeBytes, string mimeType, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task MarkFailedAsync(Guid attachmentId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<Guid> CreateUploadedAsync(NewUploadedAttachment input, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<int> ReassignToEventAsync(IReadOnlyList<Guid> attachmentIds, Guid ticketId, long eventId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<int> ReassignToMailAsync(IReadOnlyList<AttachmentReassignToMail> assignments, Guid ticketId, Guid mailMessageId, long ticketEventId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<Guid> CreateForKbArticleAsync(NewKbArticleAttachment input, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AttachmentRow>> ListByKbArticleAsync(Guid articleId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> DeleteKbAttachmentAsync(Guid attachmentId, Guid articleId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<Guid> CreateForFeedbackEntryAsync(NewFeedbackEntryAttachment input, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AttachmentRow>> ListByFeedbackEntryAsync(Guid entryId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> DeleteFeedbackAttachmentAsync(Guid attachmentId, Guid entryId, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeBlobStore : IBlobStore
    {
        public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("blob")));
        public Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<bool> ExistsAsync(string contentHash, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<bool> DeleteAsync(string contentHash, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
