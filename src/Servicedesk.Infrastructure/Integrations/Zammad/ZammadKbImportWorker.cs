using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Mail.Attachments;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Storage;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Background worker that drives the article-import phase of a Zammad KB
/// import. Reads run-ids from <see cref="IZammadKbImportQueue"/>; for each
/// run loads the article selection + section mappings, fetches the KB
/// /init bundle once, and processes the selected answers sequentially.
///
/// Per-article work:
/// <list type="number">
/// <item>Idempotency check on <c>kb_article_import_mappings</c>.</item>
/// <item>Section resolution via <c>kb_section_import_mappings</c>.</item>
/// <item>Pick the default-locale translation; skip when missing/empty.</item>
/// <item>Email-match Zammad author against local users.</item>
/// <item>Insert <c>kb_articles</c> at the mapped status with an
/// import-unique slug, derive body_text, write attachment blobs, rewrite
/// inline image URLs, upsert <c>kb_article_translations</c>.</item>
/// <item>Mark featured if Zammad's <c>promoted</c> flag was set.</item>
/// <item>Persist <c>external_author_metadata</c> JSONB when the local
/// user match failed so the audit trail survives.</item>
/// <item>Record the outcome in <c>kb_import_records</c> and bump the run
/// totals JSONB.</item>
/// </list>
public sealed class ZammadKbImportWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IZammadKbImportQueue _queue;
    private readonly ILogger<ZammadKbImportWorker> _logger;

    private const int TotalsFlushBatchSize = 10;
    /// Worker-side cap to keep the integration's Zammad-side load bounded.
    /// 5,000 mirrors the ticket-side cap; a single Zammad KB rarely holds
    /// more articles than that on a customer-grade install.
    private const int MaxAnswersPerRun = 5_000;

    public ZammadKbImportWorker(
        IServiceProvider sp,
        IZammadKbImportQueue queue,
        ILogger<ZammadKbImportWorker> logger)
    {
        _sp = sp;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same staggered-start as the ticket-side worker — gives the
        // bootstrap, settings cache and secret store warmup time before
        // the first run could land.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await foreach (var runId in _queue.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) return;
                await ProcessRunAsync(runId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ZammadKbImportWorker main loop terminated unexpectedly; worker is offline until restart.");
            throw;
        }
    }

    private async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var api = scope.ServiceProvider.GetRequiredService<IZammadApiClient>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var sectionRepo = scope.ServiceProvider.GetRequiredService<IKbSectionRepository>();
        var articleRepo = scope.ServiceProvider.GetRequiredService<IKbArticleRepository>();
        var attachments = scope.ServiceProvider.GetRequiredService<IAttachmentRepository>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var sanitizer = scope.ServiceProvider.GetRequiredService<IKbHtmlSanitizer>();

        try
        {
            if (!await settings.GetAsync<bool>(SettingKeys.Zammad.Enabled, ct))
            {
                await MarkFailedAsync(ds, runId,
                    "Zammad integration is disabled. Toggle it on first.", ct);
                return;
            }

            var run = await LoadRunAsync(ds, runId, ct);
            if (run is null) return;
            if (run.Status != "importing")
            {
                _logger.LogInformation(
                    "KB-import run {RunId} picked up in non-importing status '{Status}'; skipping.",
                    runId, run.Status);
                return;
            }

            // article_selection captures the picker snapshot.
            var selection = ParseSelection(run.ArticleSelectionJson);
            if (selection.AnswerIds.Count == 0)
            {
                await MarkFailedAsync(ds, runId, "Article selection is empty.", ct);
                return;
            }
            if (run.SourceKbId is null)
            {
                await MarkFailedAsync(ds, runId, "Run has no source KB.", ct);
                return;
            }

            var sectionMappings = await LoadSectionMappingsAsync(ds, ct);
            var defaultLocale = await LoadDefaultLocaleAsync(ds, ct);
            var importingUserId = run.StartedByUserId
                ?? await ResolveSystemUserIdAsync(ds, ct);

            // Single /init call covers every answer in the selection; the
            // selection-size cap above keeps the in-memory dataset bounded.
            var init = await api.GetKnowledgeBaseInitAsync(ct);
            var answersById = init.Answers.ToDictionary(a => a.Id);

            var totals = ZammadKbImportTotals.Empty(selection.AnswerIds.Count);
            var batchSinceFlush = 0;

            foreach (var answerId in selection.AnswerIds.Take(MaxAnswersPerRun))
            {
                ct.ThrowIfCancellationRequested();
                if (await IsCancelledAsync(ds, runId, ct))
                {
                    await FlushTotalsAsync(ds, runId, totals, ct);
                    return;
                }

                if (!answersById.TryGetValue(answerId, out var answer))
                {
                    totals = totals with
                    {
                        Failed = totals.Failed + 1,
                        Processed = totals.Processed + 1,
                    };
                    await InsertRecordAsync(ds, runId,
                        answerId: answerId,
                        categoryId: null,
                        title: null,
                        result: ZammadKbImportRecordResult.Failed,
                        reasons: new[] { "answer_not_in_init_payload" },
                        mapping: null,
                        targetArticleId: null,
                        ct: ct);
                    if (++batchSinceFlush >= TotalsFlushBatchSize)
                    {
                        await FlushTotalsAsync(ds, runId, totals, ct);
                        batchSinceFlush = 0;
                    }
                    continue;
                }

                var outcome = await ImportAnswerAsync(
                    answer, defaultLocale, importingUserId,
                    run.SourceKbId!.Value,
                    sectionMappings, ds, api, sectionRepo, articleRepo,
                    attachments, blobs, sanitizer, runId, ct);

                totals = totals.WithOutcome(outcome.Result);
                await InsertRecordAsync(ds, runId,
                    answerId: answer.Id,
                    categoryId: answer.CategoryId,
                    title: outcome.Title,
                    result: outcome.Result,
                    reasons: outcome.Reasons,
                    mapping: outcome.Mapping,
                    targetArticleId: outcome.ArticleId,
                    ct: ct);

                if (++batchSinceFlush >= TotalsFlushBatchSize)
                {
                    await FlushTotalsAsync(ds, runId, totals, ct);
                    batchSinceFlush = 0;
                }
            }

            await FlushTotalsAsync(ds, runId, totals, ct);
            await SetStatusAsync(ds, runId, "completed", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SafeSetStatusAsync(ds, runId, "cancelled", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KB-import run {RunId} failed.", runId);
            await SafeSetFailedAsync(ds, runId, ex.Message, CancellationToken.None);
        }
    }

    // ---- per-answer import -------------------------------------------

    private async Task<AnswerOutcome> ImportAnswerAsync(
        ZammadKbAnswer answer,
        string defaultLocale,
        Guid importingUserId,
        long sourceKbId,
        IReadOnlyDictionary<long, SectionMapping> sectionMappings,
        NpgsqlDataSource ds,
        IZammadApiClient api,
        IKbSectionRepository sectionRepo,
        IKbArticleRepository articleRepo,
        IAttachmentRepository attachments,
        IBlobStore blobs,
        IKbHtmlSanitizer sanitizer,
        Guid runId,
        CancellationToken ct)
    {
        // Pick translation matching default locale (case-insensitive,
        // also accept loose prefix match like 'nl' → 'nl-BE').
        var translation = PickTranslation(answer.Translations, defaultLocale);
        var title = translation?.Title?.Trim() ?? string.Empty;
        var bodyHtml = translation?.BodyHtml ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title))
        {
            return new AnswerOutcome(
                Result: ZammadKbImportRecordResult.SkippedNoTranslation,
                ArticleId: null,
                Title: null,
                Reasons: new[] { $"no_translation_for_locale:{defaultLocale}" },
                Mapping: new Dictionary<string, object?> { ["defaultLocale"] = defaultLocale });
        }

        // /init only ships translation metadata (titles + ids) for KB
        // answers, not body HTML. Bodies live on a separate
        // KnowledgeBaseAnswerTranslationContent row that has to be
        // fetched per-answer via `?include_contents={translation_id}`.
        // Also re-pulls the attachment manifest for this answer in case
        // /init's copy was incomplete.
        var contentWarnings = new List<string>();
        IReadOnlyList<ZammadKbAnswerAttachment> attachmentManifest = answer.Attachments;
        if (translation is not null && translation.Id > 0)
        {
            try
            {
                var detail = await api.GetKnowledgeBaseAnswerWithContentAsync(
                    knowledgeBaseId: sourceKbId,
                    answerId: answer.Id,
                    translationId: translation.Id,
                    ct: ct);
                if (detail is not null)
                {
                    if (!string.IsNullOrEmpty(detail.BodyHtml))
                    {
                        bodyHtml = detail.BodyHtml!;
                    }
                    if (detail.Attachments.Count > 0)
                    {
                        attachmentManifest = detail.Attachments;
                    }
                }
                else
                {
                    contentWarnings.Add("answer_detail_404");
                }
            }
            catch (Exception ex)
            {
                contentWarnings.Add($"answer_detail_fetch_failed:{ex.GetType().Name}");
                _logger.LogWarning(ex,
                    "Failed to fetch KB answer detail {AnswerId} (translation {TranslationId}).",
                    answer.Id, translation.Id);
            }
        }

        // Idempotency: if we've already imported this Zammad answer,
        // surface 'already_imported' without touching the article.
        const string existing = """
            SELECT target_article_id FROM kb_article_import_mappings
             WHERE zammad_answer_id = @AnswerId
            """;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            var existingId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                existing, new { AnswerId = answer.Id }, cancellationToken: ct));
            if (existingId is not null)
            {
                return new AnswerOutcome(
                    Result: ZammadKbImportRecordResult.AlreadyImported,
                    ArticleId: existingId.Value,
                    Title: title,
                    Reasons: Array.Empty<string>(),
                    Mapping: new Dictionary<string, object?> { ["existingArticleId"] = existingId.Value });
            }
        }

        // Section resolution via the approved mappings.
        if (!sectionMappings.TryGetValue(answer.CategoryId, out var sec))
        {
            return new AnswerOutcome(
                Result: ZammadKbImportRecordResult.SkippedNoSectionMapping,
                ArticleId: null,
                Title: title,
                Reasons: new[] { $"no_mapping_for_category:{answer.CategoryId}" },
                Mapping: new Dictionary<string, object?> { ["zammadCategoryId"] = answer.CategoryId });
        }
        if (sec.Action == "skip")
        {
            return new AnswerOutcome(
                Result: ZammadKbImportRecordResult.SkippedSectionSkipped,
                ArticleId: null,
                Title: title,
                Reasons: new[] { $"category_skipped:{answer.CategoryId}" },
                Mapping: new Dictionary<string, object?> { ["zammadCategoryId"] = answer.CategoryId });
        }
        if (sec.TargetSectionId is null)
        {
            return new AnswerOutcome(
                Result: ZammadKbImportRecordResult.SkippedNoSectionMapping,
                ArticleId: null,
                Title: title,
                Reasons: new[] { $"section_mapping_has_no_target:{answer.CategoryId}" },
                Mapping: new Dictionary<string, object?> { ["zammadCategoryId"] = answer.CategoryId });
        }

        var status = ZammadKbStatusMapper.Map(answer.InternalAt, answer.PublishedAt, answer.ArchivedAt);
        var sectionId = sec.TargetSectionId.Value;
        var slug = await DeriveUniqueSlugAsync(ds, sectionId, title, ct);

        // Create the article first so attachments can carry the article
        // id. internal_note + status flow are propagated separately.
        var article = await articleRepo.CreateArticleAsync(
            sectionId: sectionId,
            slug: slug,
            status: status,
            editorNotes: NullIfEmpty(answer.InternalNote),
            position: answer.Position,
            actorUserId: importingUserId,
            ct: ct);

        if (answer.Promoted && status == ZammadKbStatusMapper.Published)
        {
            // is_featured only renders on the landing tile for published
            // articles; flipping it on an Internal/Draft is harmless but
            // gives no UI surface so we skip it for those statuses.
            await articleRepo.SetFeaturedAsync(article.Id, isFeatured: true, importingUserId, ct);
        }

        // Fetch + write each attachment first so we have a complete
        // (zammad_attachment_id → local Guid) map for the body rewriter.
        var attachmentMap = new Dictionary<long, Guid>();
        var cidMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var attachmentWarnings = new List<string>();
        var maxBytes = await ReadMaxAttachmentBytesAsync(ds, ct);

        foreach (var att in attachmentManifest)
        {
            ct.ThrowIfCancellationRequested();
            if (att.SizeBytes > maxBytes && maxBytes > 0)
            {
                attachmentWarnings.Add($"attachment_skipped_too_large:{att.Id}:{att.SizeBytes}");
                continue;
            }
            try
            {
                var localId = await FetchAndStoreAttachmentAsync(
                    api, blobs, attachments, article.Id,
                    upstreamId: att.Id,
                    advertisedMime: att.MimeType,
                    advertisedFilename: SanitizeFilename(att.Filename),
                    ct: ct);
                attachmentMap[att.Id] = localId;
                if (!string.IsNullOrWhiteSpace(att.ContentId))
                {
                    cidMap[att.ContentId!] = localId;
                }
            }
            catch (Exception ex)
            {
                attachmentWarnings.Add($"attachment_fetch_failed:{att.Id}:{ex.GetType().Name}");
                _logger.LogWarning(ex,
                    "Failed to fetch KB attachment {AttId} for answer {AnswerId}.", att.Id, answer.Id);
            }
        }

        // Body-URL scan: many Zammad answers carry an empty
        // `attachments[]` manifest while still referencing inline images
        // by their Store::File id in the body HTML (e.g.
        // `<img src="/api/v1/attachments/253306">`). Scan the body for
        // those references and fetch any that the manifest didn't
        // already cover. Same fetch endpoint, same map — only the
        // discovery path differs.
        foreach (var urlId in ExtractAttachmentIdsFromHtml(bodyHtml))
        {
            ct.ThrowIfCancellationRequested();
            if (attachmentMap.ContainsKey(urlId)) continue;
            try
            {
                var localId = await FetchAndStoreAttachmentAsync(
                    api, blobs, attachments, article.Id,
                    upstreamId: urlId,
                    advertisedMime: null,
                    advertisedFilename: $"zammad-{urlId}",
                    ct: ct);
                attachmentMap[urlId] = localId;
            }
            catch (Exception ex)
            {
                attachmentWarnings.Add($"body_attachment_fetch_failed:{urlId}:{ex.GetType().Name}");
                _logger.LogWarning(ex,
                    "Failed to fetch body-referenced KB attachment {UpstreamId} for answer {AnswerId}.",
                    urlId, answer.Id);
            }
        }

        // Rewrite + sanitize body. The rewriter rewrites both `cid:` and
        // `/api/v1/attachments/{id}` references; whatever stays unresolved
        // gets stripped by the sanitizer (img with non-local src is not
        // on the allow-list).
        var rewritten = ZammadKbHtmlRewriter.Rewrite(bodyHtml, article.Id, attachmentMap, cidMap);
        var sanitized = sanitizer.Sanitize(rewritten.RewrittenHtml);
        var bodyText = KbBodyStripper.HtmlToText(sanitized);

        await articleRepo.UpsertTranslationAsync(
            articleId: article.Id,
            localeCode: defaultLocale,
            title: title,
            bodyHtml: sanitized,
            bodyText: bodyText,
            ct: ct);

        // Author resolve. Zammad's /init bundle includes assets but we
        // don't fully unpack the user-asset map here — we look up the
        // author by created_by_id only to read their email from a small
        // per-run cache. When the author can't be found locally we
        // persist external_author_metadata so the trail is preserved.
        var (createdByUserId, externalMeta) = await ResolveAuthorAsync(
            ds, api, answer.CreatedById, ct);
        if (createdByUserId is null && externalMeta is not null)
        {
            await SetExternalAuthorMetadataAsync(ds, article.Id, externalMeta, ct);
        }
        else if (createdByUserId is not null)
        {
            // Stamp created_by_user_id directly — the repository's
            // CreateArticleAsync already set it to the importing admin;
            // overwrite with the resolved Zammad author when known.
            await OverwriteCreatedByAsync(ds, article.Id, createdByUserId.Value, ct);
        }

        // Idempotency mapping row + content-hash for future change-detect.
        var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n\n" + (sanitized ?? string.Empty)));
        const string upsertMapping = """
            INSERT INTO kb_article_import_mappings
                (zammad_answer_id, target_article_id, content_hash, run_id, imported_utc)
            VALUES (@AnswerId, @ArticleId, @Hash, @RunId, now())
            ON CONFLICT (zammad_answer_id) DO NOTHING
            """;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition(upsertMapping, new
            {
                AnswerId = answer.Id,
                ArticleId = article.Id,
                Hash = contentHash,
                RunId = runId,
            }, cancellationToken: ct));
        }

        var mapping = new Dictionary<string, object?>
        {
            ["targetSectionId"] = sectionId,
            ["targetArticleId"] = article.Id,
            ["status"] = status,
            ["promoted"] = answer.Promoted,
            ["slug"] = slug,
            ["bodyRewriteCount"] = rewritten.RewriteCount,
            ["unresolvedCidCount"] = rewritten.UnresolvedCidCount,
            ["unresolvedPreviewCount"] = rewritten.UnresolvedPreviewCount,
            ["attachmentsImported"] = attachmentMap.Count,
        };
        if (attachmentWarnings.Count > 0) mapping["attachmentWarnings"] = attachmentWarnings;
        if (contentWarnings.Count > 0) mapping["contentWarnings"] = contentWarnings;
        if (externalMeta is not null) mapping["externalAuthor"] = externalMeta;
        mapping["bodyByteLength"] = bodyHtml.Length;

        var reasons = new List<string>();
        if (rewritten.UnresolvedCidCount > 0) reasons.Add($"unresolved_cid:{rewritten.UnresolvedCidCount}");
        if (rewritten.UnresolvedPreviewCount > 0) reasons.Add($"unresolved_preview_url:{rewritten.UnresolvedPreviewCount}");
        reasons.AddRange(attachmentWarnings);
        reasons.AddRange(contentWarnings);

        return new AnswerOutcome(
            Result: ZammadKbImportRecordResult.Imported,
            ArticleId: article.Id,
            Title: title,
            Reasons: reasons,
            Mapping: mapping);
    }

    private static ZammadKbAnswerTranslation? PickTranslation(
        IReadOnlyList<ZammadKbAnswerTranslation> translations,
        string defaultLocale)
    {
        if (translations.Count == 0) return null;
        // Exact match first.
        var hit = translations.FirstOrDefault(t =>
            string.Equals(t.LocaleCode, defaultLocale, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        // Loose prefix match — "nl" matches "nl-BE", "nl-NL", etc.
        var hyphen = defaultLocale.IndexOf('-');
        if (hyphen > 0)
        {
            var prefix = defaultLocale[..hyphen];
            hit = translations.FirstOrDefault(t =>
                t.LocaleCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        // Final fallback — the first translation with a non-empty title.
        return translations.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Title));
    }

    // ---- helpers ------------------------------------------------------

    private static async Task<long> ReadMaxAttachmentBytesAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var raw = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = 'Storage.MaxAttachmentBytes'",
            cancellationToken: ct));
        if (long.TryParse(raw, out var n) && n > 0) return n;
        return 26_214_400; // 25 MB safety net, mirrors KbAttachmentEndpoints
    }

    private static async Task<string> DeriveUniqueSlugAsync(
        NpgsqlDataSource ds, Guid sectionId, string title, CancellationToken ct)
    {
        var baseSlug = KbSlugGenerator.Slugify(title);
        await using var conn = await ds.OpenConnectionAsync(ct);
        var existing = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT slug FROM kb_articles WHERE section_id = @SectionId",
            new { SectionId = sectionId }, cancellationToken: ct)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseSlug)) return baseSlug;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = baseSlug + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!existing.Contains(candidate)) return candidate;
        }
        return baseSlug + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    /// Scans a body HTML string for Zammad attachment URL references —
    /// pulls every `/api/v1/attachments/<id>` token out and returns the
    /// distinct numeric ids. Tolerates query strings (`?preview=1`).
    /// The set is empty when the body is empty or carries no matches.
    private static IReadOnlyCollection<long> ExtractAttachmentIdsFromHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return Array.Empty<long>();
        var set = new HashSet<long>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(html, @"/api/v1/attachments/(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (long.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id))
            {
                set.Add(id);
            }
        }
        return set;
    }

    /// Streams one Zammad attachment by upstream id into the local blob
    /// store and persists the `attachments` row with `owner_kind='KbArticle'`.
    /// Sniffs the MIME type from the first 512 bytes via
    /// <see cref="Servicedesk.Infrastructure.Storage.MimeSniffer"/>; the
    /// advertised MIME (from the upstream manifest or Content-Type
    /// header) is the fallback. Returns the new local attachment id.
    private static async Task<Guid> FetchAndStoreAttachmentAsync(
        IZammadApiClient api,
        IBlobStore blobs,
        IAttachmentRepository attachments,
        Guid articleId,
        long upstreamId,
        string? advertisedMime,
        string advertisedFilename,
        CancellationToken ct)
    {
        await using var source = await api.FetchKnowledgeBaseAttachmentBytesAsync(
            knowledgeBaseId: 0, // unused on the new path (/api/v1/attachments/{id})
            answerId: 0,
            attachmentId: upstreamId,
            ct: ct);

        // Sniff the first 512 bytes for the actual content-type so a
        // PNG advertised as octet-stream still lands with the right
        // mime. The ConcatStream wrapper re-prefixes the sniffed head
        // onto the rest of the upstream stream before writing to the
        // blob store.
        var headBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(
            Storage.MimeSniffer.SniffWindowBytes);
        try
        {
            var headLen = await ReadFullyAsync(source,
                headBuffer.AsMemory(0, Storage.MimeSniffer.SniffWindowBytes), ct);
            var sniffedMime = Storage.MimeSniffer.Sniff(
                headBuffer.AsSpan(0, headLen),
                advertisedMime,
                advertisedFilename);

            using var combined = new HeadedStream(
                headBuffer.AsMemory(0, headLen), source);
            var write = await blobs.WriteAsync(combined, ct);
            return await attachments.CreateForKbArticleAsync(new NewKbArticleAttachment(
                ArticleId: articleId,
                ContentHash: write.ContentHash,
                SizeBytes: write.SizeBytes,
                MimeType: sniffedMime,
                OriginalFilename: advertisedFilename), ct);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(headBuffer);
        }
    }

    private static async Task<int> ReadFullyAsync(Stream source, Memory<byte> dest, CancellationToken ct)
    {
        var total = 0;
        while (total < dest.Length)
        {
            var read = await source.ReadAsync(dest[total..], ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// Read-only stream that emits a buffered "head" first and then
    /// drains the underlying inner stream. Used to re-prefix the MIME-
    /// sniff peek onto the rest of the upstream byte stream before the
    /// blob-store write consumes it.
    private sealed class HeadedStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _head;
        private readonly Stream _tail;
        private int _headOffset;

        public HeadedStream(ReadOnlyMemory<byte> head, Stream tail)
        {
            _head = head;
            _tail = tail;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_headOffset < _head.Length)
            {
                var take = Math.Min(count, _head.Length - _headOffset);
                _head.Span.Slice(_headOffset, take).CopyTo(buffer.AsSpan(offset, take));
                _headOffset += take;
                return take;
            }
            return _tail.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_headOffset < _head.Length)
            {
                var take = Math.Min(buffer.Length, _head.Length - _headOffset);
                _head.Slice(_headOffset, take).CopyTo(buffer);
                _headOffset += take;
                return take;
            }
            return await _tail.ReadAsync(buffer, ct);
        }
    }

    private static async Task<(Guid? UserId, Dictionary<string, object?>? ExternalMeta)> ResolveAuthorAsync(
        NpgsqlDataSource ds, IZammadApiClient api, long? zammadUserId, CancellationToken ct)
    {
        if (zammadUserId is null) return (null, null);
        ZammadUser? zUser;
        try { zUser = await api.GetUserAsync(zammadUserId.Value, ct); }
        catch { zUser = null; }
        if (zUser is null)
        {
            return (null, new Dictionary<string, object?>
            {
                ["source"] = "zammad",
                ["zammadUserId"] = zammadUserId.Value,
            });
        }

        if (!string.IsNullOrWhiteSpace(zUser.Email))
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var localId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM users WHERE LOWER(email) = LOWER(@Email)",
                new { Email = zUser.Email!.Trim() }, cancellationToken: ct));
            if (localId is not null) return (localId, null);
        }

        var meta = new Dictionary<string, object?>
        {
            ["source"] = "zammad",
            ["zammadUserId"] = zammadUserId.Value,
            ["email"] = zUser.Email,
            ["name"] = string.Join(" ", new[] { zUser.FirstName, zUser.LastName }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
        };
        return (null, meta);
    }

    private static async Task SetExternalAuthorMetadataAsync(
        NpgsqlDataSource ds, Guid articleId, Dictionary<string, object?> meta, CancellationToken ct)
    {
        const string sql = """
            UPDATE kb_articles
               SET external_author_metadata = @Meta::jsonb,
                   created_by_user_id = NULL
             WHERE id = @ArticleId
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            ArticleId = articleId,
            Meta = JsonSerializer.Serialize(meta),
        }, cancellationToken: ct));
    }

    private static async Task OverwriteCreatedByAsync(
        NpgsqlDataSource ds, Guid articleId, Guid userId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE kb_articles SET created_by_user_id = @UserId WHERE id = @ArticleId",
            new { ArticleId = articleId, UserId = userId }, cancellationToken: ct));
    }

    private static async Task<RunLoadRow?> LoadRunAsync(NpgsqlDataSource ds, Guid runId, CancellationToken ct)
    {
        const string sql = """
            SELECT id                  AS "Id",
                   status              AS "Status",
                   source_kb_id        AS "SourceKbId",
                   started_by_user_id  AS "StartedByUserId",
                   article_selection::text AS "ArticleSelectionJson"
              FROM kb_import_runs
             WHERE id = @RunId
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RunLoadRow>(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
    }

    private static async Task<IReadOnlyDictionary<long, SectionMapping>> LoadSectionMappingsAsync(
        NpgsqlDataSource ds, CancellationToken ct)
    {
        const string sql = """
            SELECT zammad_category_id AS "ZammadCategoryId",
                   target_section_id  AS "TargetSectionId",
                   action             AS "Action"
              FROM kb_section_import_mappings
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SectionMapping>(new CommandDefinition(
            sql, cancellationToken: ct));
        return rows.ToDictionary(r => r.ZammadCategoryId);
    }

    private static async Task<string> LoadDefaultLocaleAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var code = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT default_locale_code FROM knowledge_base LIMIT 1", cancellationToken: ct));
        return string.IsNullOrWhiteSpace(code) ? "nl-BE" : code;
    }

    private static async Task<Guid> ResolveSystemUserIdAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        // Fallback when the run wasn't tied to a user (shouldn't happen
        // via the UI but the path is defensive). Picks the oldest admin.
        await using var conn = await ds.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM users WHERE role_name = 'Admin' ORDER BY created_utc ASC LIMIT 1",
            cancellationToken: ct));
        if (id is null)
        {
            throw new InvalidOperationException(
                "No admin user available to attribute the KB import to.");
        }
        return id.Value;
    }

    private static ZammadKbArticleSelection ParseSelection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new ZammadKbArticleSelection(Array.Empty<long>(), null);
        try
        {
            return JsonSerializer.Deserialize<ZammadKbArticleSelection>(json)
                ?? new ZammadKbArticleSelection(Array.Empty<long>(), null);
        }
        catch (JsonException)
        {
            return new ZammadKbArticleSelection(Array.Empty<long>(), null);
        }
    }

    private static async Task<bool> IsCancelledAsync(NpgsqlDataSource ds, Guid runId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var s = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM kb_import_runs WHERE id = @RunId", new { RunId = runId }, cancellationToken: ct));
        return string.Equals(s, "cancelled", StringComparison.Ordinal);
    }

    private static async Task FlushTotalsAsync(NpgsqlDataSource ds, Guid runId, ZammadKbImportTotals totals, CancellationToken ct)
    {
        const string sql = "UPDATE kb_import_runs SET totals = @Totals::jsonb WHERE id = @RunId";
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            Totals = JsonSerializer.Serialize(totals),
        }, cancellationToken: ct));
    }

    private static async Task SetStatusAsync(NpgsqlDataSource ds, Guid runId, string status, CancellationToken ct)
    {
        const string sql = """
            UPDATE kb_import_runs
               SET status = @Status,
                   finished_utc = CASE WHEN @Status IN ('completed','failed','cancelled')
                                       THEN COALESCE(finished_utc, now())
                                       ELSE finished_utc
                                  END
             WHERE id = @RunId
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { RunId = runId, Status = status }, cancellationToken: ct));
    }

    private static async Task SafeSetStatusAsync(NpgsqlDataSource ds, Guid runId, string status, CancellationToken ct)
    {
        try { await SetStatusAsync(ds, runId, status, ct); }
        catch { /* best-effort */ }
    }

    private static async Task MarkFailedAsync(NpgsqlDataSource ds, Guid runId, string error, CancellationToken ct)
    {
        const string sql = """
            UPDATE kb_import_runs
               SET status = 'failed',
                   finished_utc = COALESCE(finished_utc, now()),
                   error_message = @Error
             WHERE id = @RunId
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { RunId = runId, Error = error }, cancellationToken: ct));
    }

    private static async Task SafeSetFailedAsync(NpgsqlDataSource ds, Guid runId, string error, CancellationToken ct)
    {
        try { await MarkFailedAsync(ds, runId, error, ct); }
        catch { /* best-effort */ }
    }

    private static async Task InsertRecordAsync(
        NpgsqlDataSource ds, Guid runId,
        long answerId, long? categoryId, string? title,
        string result, IReadOnlyList<string> reasons,
        IReadOnlyDictionary<string, object?>? mapping,
        Guid? targetArticleId,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO kb_import_records
                (run_id, zammad_answer_id, zammad_category_id, zammad_title,
                 result, unresolved_reasons, mapping, target_article_id, created_utc)
            VALUES
                (@RunId, @AnswerId, @CategoryId, @Title,
                 @Result, @Reasons, @Mapping::jsonb, @ArticleId, now())
            """;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            AnswerId = answerId,
            CategoryId = categoryId,
            Title = title,
            Result = result,
            Reasons = reasons.ToArray(),
            Mapping = JsonSerializer.Serialize(mapping ?? new Dictionary<string, object?>()),
            ArticleId = targetArticleId,
        }, cancellationToken: ct));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string SanitizeFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "attachment";
        var leaf = Path.GetFileName(input);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = leaf.Select(c => invalid.Contains(c) || c == '<' || c == '>' || c == ':' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimStart('.');
        if (string.IsNullOrEmpty(s)) s = "attachment";
        return s.Length > 200 ? s[..200] : s;
    }

    private sealed class RunLoadRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public long? SourceKbId { get; set; }
        public Guid? StartedByUserId { get; set; }
        public string? ArticleSelectionJson { get; set; }
    }

    public sealed class SectionMapping
    {
        public long ZammadCategoryId { get; set; }
        public Guid? TargetSectionId { get; set; }
        public string Action { get; set; } = string.Empty;
    }

    private sealed record AnswerOutcome(
        string Result,
        Guid? ArticleId,
        string? Title,
        IReadOnlyList<string> Reasons,
        IReadOnlyDictionary<string, object?> Mapping);
}

internal static class ZammadKbImportTotalsExtensions
{
    public static ZammadKbImportTotals WithOutcome(this ZammadKbImportTotals totals, string outcome)
    {
        var processed = totals.Processed + 1;
        return outcome switch
        {
            ZammadKbImportRecordResult.Imported =>
                totals with { Imported = totals.Imported + 1, Processed = processed },
            ZammadKbImportRecordResult.AlreadyImported =>
                totals with { AlreadyImported = totals.AlreadyImported + 1, Processed = processed },
            ZammadKbImportRecordResult.SkippedNoSectionMapping =>
                totals with { SkippedNoSectionMapping = totals.SkippedNoSectionMapping + 1, Processed = processed },
            ZammadKbImportRecordResult.SkippedNoTranslation =>
                totals with { SkippedNoTranslation = totals.SkippedNoTranslation + 1, Processed = processed },
            ZammadKbImportRecordResult.SkippedSectionSkipped =>
                totals with { SkippedSectionSkipped = totals.SkippedSectionSkipped + 1, Processed = processed },
            _ => totals with { Failed = totals.Failed + 1, Processed = processed },
        };
    }
}
