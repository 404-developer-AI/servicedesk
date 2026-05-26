using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Persistence.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

public sealed class ZammadKbImportService : IZammadKbImportService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IZammadApiClient _apiClient;
    private readonly IZammadKbImportQueue _queue;
    private readonly IKbSectionRepository _sectionRepository;
    private readonly ISettingsService _settings;
    private readonly ILogger<ZammadKbImportService> _logger;

    public ZammadKbImportService(
        NpgsqlDataSource dataSource,
        IZammadApiClient apiClient,
        IZammadKbImportQueue queue,
        IKbSectionRepository sectionRepository,
        ISettingsService settings,
        ILogger<ZammadKbImportService> logger)
    {
        _dataSource = dataSource;
        _apiClient = apiClient;
        _queue = queue;
        _sectionRepository = sectionRepository;
        _settings = settings;
        _logger = logger;
    }

    public async Task<Guid> StartRunAsync(Guid? startedByUserId, CancellationToken ct)
    {
        await EnsureZammadEnabledAsync(ct);

        const string sql = """
            INSERT INTO kb_import_runs
                (status, started_by_user_id, started_utc, totals)
            VALUES
                ('pending', @StartedByUserId, now(), @Totals::jsonb)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            StartedByUserId = startedByUserId,
            Totals = JsonSerializer.Serialize(ZammadKbImportTotals.Empty(null)),
        }, cancellationToken: ct));
        return id;
    }

    public Task<IReadOnlyList<ZammadKnowledgeBase>> ListKnowledgeBasesAsync(CancellationToken ct)
        => _apiClient.ListKnowledgeBasesAsync(ct);

    public async Task<ZammadKbProposal?> BuildProposalAsync(
        Guid runId, long knowledgeBaseId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM kb_import_runs WHERE id = @RunId",
            new { RunId = runId }, cancellationToken: ct));
        if (status is null) return null;
        if (!(status == "pending" || status == "proposing" || status == "awaiting_approval"))
        {
            // Already past the proposal stage — return whatever was last
            // persisted so the SPA can re-render the review step.
            return await GetProposalAsync(runId, ct);
        }

        await SetRunStatusAsync(conn, runId, "proposing", ct);

        var init = await _apiClient.GetKnowledgeBaseInitAsync(ct);
        var kb = init.KnowledgeBases.FirstOrDefault(k => k.Id == knowledgeBaseId);
        if (kb is null)
        {
            // Fall back to the /knowledge_bases header in case /init's
            // payload doesn't include KB metadata (some Zammad versions).
            var heads = await _apiClient.ListKnowledgeBasesAsync(ct);
            kb = heads.FirstOrDefault(k => k.Id == knowledgeBaseId);
        }
        if (kb is null)
        {
            await FailRunAsync(conn, runId, $"Knowledge base {knowledgeBaseId} not found on Zammad.", ct);
            return null;
        }

        var categories = init.Categories
            .Where(c => c.KnowledgeBaseId == kb.Id || c.KnowledgeBaseId == 0)
            .ToList();
        // Answers may carry knowledge_base_id directly or only via category linkage.
        var answerCounts = new Dictionary<long, int>();
        foreach (var ans in init.Answers)
        {
            if (ans.KnowledgeBaseId != 0 && ans.KnowledgeBaseId != kb.Id) continue;
            answerCounts[ans.CategoryId] = answerCounts.GetValueOrDefault(ans.CategoryId, 0) + 1;
        }

        // Pull existing decisions (if any) so re-runs preserve admin choices.
        var existingDecisions = await LoadExistingSectionDecisionsAsync(conn, ct);

        var proposal = ZammadKbSectionProposalBuilder.Build(
            knowledgeBase: kb,
            categories: categories,
            answerCountByCategory: answerCounts,
            existingDecisions: existingDecisions,
            localePreference: kb.DefaultLocale);

        const string update = """
            UPDATE kb_import_runs
               SET status = 'awaiting_approval',
                   source_kb_id = @KbId,
                   source_kb_name = @KbName,
                   proposed_tree = @Tree::jsonb
             WHERE id = @RunId
            """;
        await conn.ExecuteAsync(new CommandDefinition(update, new
        {
            RunId = runId,
            KbId = kb.Id,
            KbName = kb.Name,
            Tree = JsonSerializer.Serialize(new ProposalEnvelope(proposal)),
        }, cancellationToken: ct));

        return proposal;
    }

    public async Task<bool> SaveSectionDecisionsAsync(
        Guid runId,
        IReadOnlyList<ZammadKbProposalNode> updatedNodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updatedNodes);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM kb_import_runs WHERE id = @RunId",
            new { RunId = runId }, cancellationToken: ct));
        if (status is null) return false;
        if (status != "awaiting_approval") return false;

        var current = await GetProposalAsync(runId, ct);
        if (current is null) return false;

        var byId = current.Nodes.ToDictionary(n => n.ZammadCategoryId);
        foreach (var update in updatedNodes)
        {
            // Sanitize the action to the allowed vocabulary; default to
            // "create" so a malformed POST doesn't poison the tree.
            var action = update.Action switch
            {
                "create" or "merge" or "skip" => update.Action,
                _ => "create",
            };
            // Slugs must satisfy the kb_sections CHECK constraint — re-
            // slugify on save so the admin can type whatever they want
            // into the rename field.
            var slug = KbSlugGenerator.Slugify(update.ProposedSlug);
            if (string.IsNullOrEmpty(slug) || slug == KbSlugGenerator.Fallback)
            {
                slug = KbSlugGenerator.Slugify(update.ProposedTitle);
            }
            var title = string.IsNullOrWhiteSpace(update.ProposedTitle)
                ? $"Category #{update.ZammadCategoryId}"
                : update.ProposedTitle.Trim();
            var target = action == "merge" ? update.TargetSectionId : null;

            byId[update.ZammadCategoryId] = new ZammadKbProposalNode(
                ZammadCategoryId: update.ZammadCategoryId,
                ZammadParentId: byId.TryGetValue(update.ZammadCategoryId, out var prior) ? prior.ZammadParentId : update.ZammadParentId,
                Depth: prior?.Depth ?? update.Depth,
                Position: prior?.Position ?? update.Position,
                ProposedTitle: title,
                ProposedSlug: slug,
                Action: action,
                TargetSectionId: target,
                AnswerCount: prior?.AnswerCount ?? update.AnswerCount);
        }

        var merged = current with { Nodes = byId.Values.OrderBy(n => n.Depth).ThenBy(n => n.Position).ToList() };
        const string sql = """
            UPDATE kb_import_runs
               SET proposed_tree = @Tree::jsonb
             WHERE id = @RunId
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            Tree = JsonSerializer.Serialize(new ProposalEnvelope(merged)),
        }, cancellationToken: ct));
        return true;
    }

    public async Task<int> ApplySectionsAsync(Guid runId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM kb_import_runs WHERE id = @RunId",
            new { RunId = runId }, cancellationToken: ct));
        if (status is null || status != "awaiting_approval") return 0;

        var proposal = await GetProposalAsync(runId, ct);
        if (proposal is null) return 0;

        // Apply in depth order so a parent always exists before its child.
        var ordered = proposal.Nodes.OrderBy(n => n.Depth).ThenBy(n => n.Position).ToList();
        var zammadToLocal = new Dictionary<long, Guid>();
        var sectionCount = 0;

        foreach (var node in ordered)
        {
            ct.ThrowIfCancellationRequested();
            Guid? targetSectionId = null;
            switch (node.Action)
            {
                case "create":
                {
                    Guid? parentSectionId = null;
                    if (node.ZammadParentId is long parentZammadId
                        && zammadToLocal.TryGetValue(parentZammadId, out var resolved))
                    {
                        parentSectionId = resolved;
                    }
                    // Derive a non-clashing slug within the parent.
                    var slug = await EnsureUniqueSlugAsync(conn, parentSectionId, node.ProposedSlug, ct);
                    var created = await _sectionRepository.CreateSectionAsync(
                        parentSectionId: parentSectionId,
                        slug: slug,
                        iconName: null,
                        position: node.Position,
                        actorUserId: actorUserId,
                        ct: ct);
                    var defaultLocale = await ReadDefaultLocaleAsync(conn, ct);
                    await _sectionRepository.UpsertTranslationAsync(
                        sectionId: created.Id,
                        localeCode: defaultLocale,
                        title: node.ProposedTitle,
                        description: null,
                        ct: ct);
                    targetSectionId = created.Id;
                    break;
                }
                case "merge":
                {
                    if (node.TargetSectionId is null) continue;
                    targetSectionId = node.TargetSectionId.Value;
                    break;
                }
                case "skip":
                {
                    targetSectionId = null;
                    break;
                }
            }

            // Upsert the mapping row.
            const string upsert = """
                INSERT INTO kb_section_import_mappings
                    (zammad_category_id, zammad_parent_id, zammad_title,
                     target_section_id, action, run_id, created_utc, updated_utc)
                VALUES
                    (@ZammadCategoryId, @ZammadParentId, @ZammadTitle,
                     @TargetSectionId, @Action, @RunId, now(), now())
                ON CONFLICT (zammad_category_id) DO UPDATE
                   SET target_section_id = EXCLUDED.target_section_id,
                       action            = EXCLUDED.action,
                       zammad_title      = EXCLUDED.zammad_title,
                       run_id            = EXCLUDED.run_id,
                       updated_utc       = now()
                """;
            await conn.ExecuteAsync(new CommandDefinition(upsert, new
            {
                ZammadCategoryId = node.ZammadCategoryId,
                ZammadParentId = node.ZammadParentId,
                ZammadTitle = node.ProposedTitle,
                TargetSectionId = targetSectionId,
                Action = node.Action,
                RunId = runId,
            }, cancellationToken: ct));

            if (targetSectionId is not null) zammadToLocal[node.ZammadCategoryId] = targetSectionId.Value;
            sectionCount++;
        }

        await SetRunStatusAsync(conn, runId, "approved", ct);
        return sectionCount;
    }

    public async Task<ZammadKbPickerPage> ListPickerAsync(
        Guid runId,
        string? statusFilter,
        long? categoryFilter,
        string? freeText,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var clamped = Math.Clamp(pageSize, 10, 200);
        var skip = Math.Max(0, (page - 1) * clamped);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var run = await ReadRunRowAsync(conn, runId, ct);
        if (run is null || run.SourceKbId is null)
        {
            return new ZammadKbPickerPage(Array.Empty<ZammadKbPickerItem>(), 0);
        }

        // Re-fetch /init lazily and project answers to picker items. For
        // KB sizes typical of a single-tenant Zammad (<5k answers) the
        // /init call is sub-second; we re-fetch on every picker page so
        // the importer stays stateless between runs.
        var init = await _apiClient.GetKnowledgeBaseInitAsync(ct);
        var categoryTitles = new Dictionary<long, string>();
        foreach (var cat in init.Categories)
        {
            var title = cat.Translations
                .Select(t => t.Title)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (title is not null) categoryTitles[cat.Id] = title;
        }

        IEnumerable<ZammadKbAnswer> answers = init.Answers
            .Where(a => a.KnowledgeBaseId == 0 || a.KnowledgeBaseId == run.SourceKbId.Value);

        if (categoryFilter is long catId)
        {
            answers = answers.Where(a => a.CategoryId == catId);
        }
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            answers = answers.Where(a => string.Equals(
                ZammadKbStatusMapper.Map(a.InternalAt, a.PublishedAt, a.ArchivedAt),
                statusFilter,
                StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(freeText))
        {
            var needle = freeText.Trim();
            answers = answers.Where(a => a.Translations
                .Any(t => t.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        var snap = answers.ToList();
        var total = snap.Count;
        var items = snap
            .OrderByDescending(a => a.PublishedAt ?? a.InternalAt ?? DateTimeOffset.MinValue)
            .Skip(skip).Take(clamped)
            .Select(a =>
            {
                var t = a.Translations.FirstOrDefault();
                return new ZammadKbPickerItem(
                    ZammadAnswerId: a.Id,
                    ZammadCategoryId: a.CategoryId,
                    CategoryTitle: categoryTitles.TryGetValue(a.CategoryId, out var ct2) ? ct2 : null,
                    Title: t?.Title ?? $"Answer #{a.Id}",
                    Status: ZammadKbStatusMapper.Map(a.InternalAt, a.PublishedAt, a.ArchivedAt),
                    Promoted: a.Promoted,
                    // The /init bundle carries titles but not body HTML.
                    // Bodies are fetched per-article during import via
                    // GET /knowledge_bases/answers/{id}?include_contents={tid}.
                    // For the picker, "has translation" means "has a
                    // primary-locale title" — which is what the user can
                    // see in the row label.
                    HasTranslation: !string.IsNullOrWhiteSpace(t?.Title),
                    UpdatedAt: a.PublishedAt ?? a.InternalAt ?? a.ArchivedAt);
            })
            .ToList();
        return new ZammadKbPickerPage(items, total);
    }

    public async Task<bool> StartArticleImportAsync(
        Guid runId,
        IReadOnlyList<long> answerIds,
        Guid? startedByUserId,
        CancellationToken ct)
    {
        if (answerIds.Count == 0) return false;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM kb_import_runs WHERE id = @RunId",
            new { RunId = runId }, cancellationToken: ct));
        if (status is null) return false;
        if (status != "approved") return false;

        // Persist selection + bump status. Worker reads the selection on
        // pickup so admin can navigate away while the worker drains.
        var selection = new ZammadKbArticleSelection(answerIds, Filters: null);
        var totals = ZammadKbImportTotals.Empty(answerIds.Count);
        const string sql = """
            UPDATE kb_import_runs
               SET status = 'importing',
                   article_selection = @Selection::jsonb,
                   totals = @Totals::jsonb
             WHERE id = @RunId
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId,
            Selection = JsonSerializer.Serialize(selection),
            Totals = JsonSerializer.Serialize(totals),
        }, cancellationToken: ct));

        if (!_queue.TryEnqueue(runId))
        {
            _logger.LogWarning(
                "Zammad KB-import queue refused enqueue for run {RunId}; row remains importing.", runId);
        }
        return true;
    }

    public async Task<IReadOnlyList<ZammadKbImportRunSummary>> ListRunsAsync(
        int limit, CancellationToken ct)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        const string sql = """
            SELECT r.id              AS "Id",
                   r.status          AS "Status",
                   r.started_by_user_id AS "StartedByUserId",
                   u.email           AS "StartedByDisplayName",
                   r.started_utc     AS "StartedUtc",
                   r.finished_utc    AS "FinishedUtc",
                   r.source_kb_id    AS "SourceKbId",
                   r.source_kb_name  AS "SourceKbName",
                   r.totals::text    AS "TotalsJson",
                   r.error_message   AS "ErrorMessage"
              FROM kb_import_runs r
              LEFT JOIN users u ON u.id = r.started_by_user_id
             ORDER BY r.started_utc DESC, r.id DESC
             LIMIT @Limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RunSummaryRow>(new CommandDefinition(
            sql, new { Limit = clamped }, cancellationToken: ct));
        return rows.Select(MapRunSummary).ToList();
    }

    public async Task<ZammadKbImportRunSummary?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.id              AS "Id",
                   r.status          AS "Status",
                   r.started_by_user_id AS "StartedByUserId",
                   u.email           AS "StartedByDisplayName",
                   r.started_utc     AS "StartedUtc",
                   r.finished_utc    AS "FinishedUtc",
                   r.source_kb_id    AS "SourceKbId",
                   r.source_kb_name  AS "SourceKbName",
                   r.totals::text    AS "TotalsJson",
                   r.error_message   AS "ErrorMessage"
              FROM kb_import_runs r
              LEFT JOIN users u ON u.id = r.started_by_user_id
             WHERE r.id = @RunId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RunSummaryRow>(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
        return row is null ? null : MapRunSummary(row);
    }

    public async Task<ZammadKbProposal?> GetProposalAsync(Guid runId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT proposed_tree::text FROM kb_import_runs WHERE id = @RunId",
            new { RunId = runId }, cancellationToken: ct));
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try
        {
            var env = JsonSerializer.Deserialize<ProposalEnvelope>(json);
            return env?.Proposal;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize proposed_tree on run {RunId}.", runId);
            return null;
        }
    }

    public async Task<ZammadKbImportRecordPage> ListRecordsAsync(
        Guid runId, Guid? cursor, int limit, string? resultFilter, CancellationToken ct)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        var parameters = new DynamicParameters();
        parameters.Add("RunId", runId);
        parameters.Add("Limit", clamped);

        var sql = """
            SELECT id                  AS "Id",
                   zammad_answer_id    AS "ZammadAnswerId",
                   zammad_category_id  AS "ZammadCategoryId",
                   zammad_title        AS "ZammadTitle",
                   result              AS "Result",
                   unresolved_reasons  AS "UnresolvedReasons",
                   mapping::text       AS "MappingJson",
                   target_article_id   AS "TargetArticleId",
                   created_utc         AS "CreatedUtc"
              FROM kb_import_records
             WHERE run_id = @RunId
            """;
        if (!string.IsNullOrWhiteSpace(resultFilter))
        {
            sql += " AND result = @ResultFilter";
            parameters.Add("ResultFilter", resultFilter);
        }
        if (cursor is not null)
        {
            sql += " AND id > @Cursor";
            parameters.Add("Cursor", cursor);
        }
        sql += " ORDER BY id ASC LIMIT @Limit";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<RecordRow>(new CommandDefinition(
            sql, parameters, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new ZammadKbImportRecordItem(
            Id: r.Id,
            ZammadAnswerId: r.ZammadAnswerId,
            ZammadCategoryId: r.ZammadCategoryId,
            ZammadTitle: r.ZammadTitle,
            Result: r.Result,
            UnresolvedReasons: r.UnresolvedReasons ?? Array.Empty<string>(),
            MappingJson: r.MappingJson ?? "{}",
            TargetArticleId: r.TargetArticleId,
            CreatedUtc: r.CreatedUtc)).ToList();
        var nextCursor = items.Count == clamped ? items[^1].Id : (Guid?)null;
        return new ZammadKbImportRecordPage(items, nextCursor);
    }

    public async Task<bool> CancelRunAsync(Guid runId, CancellationToken ct)
    {
        const string sql = """
            UPDATE kb_import_runs
               SET status = 'cancelled',
                   finished_utc = COALESCE(finished_utc, now())
             WHERE id = @RunId
               AND status IN ('pending','proposing','awaiting_approval','approved','importing')
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
        return n > 0;
    }

    // ---- helpers ------------------------------------------------------

    private async Task EnsureZammadEnabledAsync(CancellationToken ct)
    {
        var enabled = await _settings.GetAsync<bool>(SettingKeys.Zammad.Enabled, ct);
        if (!enabled)
        {
            throw new InvalidOperationException(
                "Zammad integration is disabled. Toggle it on under Settings → Integrations → Zammad before starting a KB import.");
        }
    }

    private static async Task SetRunStatusAsync(NpgsqlConnection conn, Guid runId, string status, CancellationToken ct)
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
        await conn.ExecuteAsync(new CommandDefinition(sql, new { RunId = runId, Status = status }, cancellationToken: ct));
    }

    private static async Task FailRunAsync(NpgsqlConnection conn, Guid runId, string error, CancellationToken ct)
    {
        const string sql = """
            UPDATE kb_import_runs
               SET status = 'failed',
                   finished_utc = COALESCE(finished_utc, now()),
                   error_message = @Error
             WHERE id = @RunId
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new { RunId = runId, Error = error }, cancellationToken: ct));
    }

    private static async Task<IReadOnlyDictionary<long, (string Action, Guid? TargetSectionId)>> LoadExistingSectionDecisionsAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT zammad_category_id AS "ZammadCategoryId",
                   action             AS "Action",
                   target_section_id  AS "TargetSectionId"
              FROM kb_section_import_mappings
            """;
        var rows = await conn.QueryAsync<MappingRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToDictionary(r => r.ZammadCategoryId, r => (r.Action, r.TargetSectionId));
    }

    private static async Task<string> EnsureUniqueSlugAsync(
        NpgsqlConnection conn, Guid? parentSectionId, string baseSlug, CancellationToken ct)
    {
        // Look up existing siblings to avoid the UNIQUE-violation on
        // (parent_section_id, slug). Suffix with -2, -3, … until free.
        const string sql = """
            SELECT slug FROM kb_sections
             WHERE (parent_section_id IS NOT DISTINCT FROM @ParentId)
            """;
        var existing = (await conn.QueryAsync<string>(new CommandDefinition(
            sql, new { ParentId = parentSectionId }, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseSlug)) return baseSlug;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = baseSlug + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!existing.Contains(candidate)) return candidate;
        }
        // Worst case: append the import-run-id fragment. Shouldn't happen
        // in practice but the loop bound keeps us out of an infinite spin.
        return baseSlug + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
    }

    private static async Task<string> ReadDefaultLocaleAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var code = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT default_locale_code FROM knowledge_base LIMIT 1", cancellationToken: ct));
        return string.IsNullOrWhiteSpace(code) ? "nl-BE" : code;
    }

    private static async Task<RunRow?> ReadRunRowAsync(NpgsqlConnection conn, Guid runId, CancellationToken ct)
    {
        const string sql = """
            SELECT id              AS "Id",
                   status          AS "Status",
                   source_kb_id    AS "SourceKbId",
                   source_kb_name  AS "SourceKbName"
              FROM kb_import_runs
             WHERE id = @RunId
            """;
        return await conn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: ct));
    }

    private static ZammadKbImportRunSummary MapRunSummary(RunSummaryRow row)
    {
        ZammadKbImportTotals totals;
        try
        {
            totals = string.IsNullOrWhiteSpace(row.TotalsJson)
                ? ZammadKbImportTotals.Empty(null)
                : JsonSerializer.Deserialize<ZammadKbImportTotals>(row.TotalsJson) ?? ZammadKbImportTotals.Empty(null);
        }
        catch (JsonException)
        {
            totals = ZammadKbImportTotals.Empty(null);
        }
        return new ZammadKbImportRunSummary(
            Id: row.Id,
            Status: ParseStatus(row.Status),
            StartedByUserId: row.StartedByUserId,
            StartedByDisplayName: row.StartedByDisplayName,
            StartedUtc: row.StartedUtc,
            FinishedUtc: row.FinishedUtc,
            SourceKbId: row.SourceKbId,
            SourceKbName: row.SourceKbName,
            Totals: totals,
            ErrorMessage: row.ErrorMessage);
    }

    private static ZammadKbImportRunStatus ParseStatus(string status) => status switch
    {
        "pending"            => ZammadKbImportRunStatus.Pending,
        "proposing"          => ZammadKbImportRunStatus.Proposing,
        "awaiting_approval"  => ZammadKbImportRunStatus.AwaitingApproval,
        "approved"           => ZammadKbImportRunStatus.Approved,
        "importing"          => ZammadKbImportRunStatus.Importing,
        "completed"          => ZammadKbImportRunStatus.Completed,
        "failed"             => ZammadKbImportRunStatus.Failed,
        "cancelled"          => ZammadKbImportRunStatus.Cancelled,
        _ => ZammadKbImportRunStatus.Pending,
    };

    // ---- Row DTOs (Dapper PascalCase aliases per project convention) ---

    private sealed class RunSummaryRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public Guid? StartedByUserId { get; set; }
        public string? StartedByDisplayName { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public long? SourceKbId { get; set; }
        public string? SourceKbName { get; set; }
        public string? TotalsJson { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed class RunRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public long? SourceKbId { get; set; }
        public string? SourceKbName { get; set; }
    }

    private sealed class RecordRow
    {
        public Guid Id { get; set; }
        public long ZammadAnswerId { get; set; }
        public long? ZammadCategoryId { get; set; }
        public string? ZammadTitle { get; set; }
        public string Result { get; set; } = string.Empty;
        public string[]? UnresolvedReasons { get; set; }
        public string? MappingJson { get; set; }
        public Guid? TargetArticleId { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    private sealed class MappingRow
    {
        public long ZammadCategoryId { get; set; }
        public string Action { get; set; } = string.Empty;
        public Guid? TargetSectionId { get; set; }
    }

    // ---- Persisted proposal envelope ----------------------------------
    //
    // Wrapping the proposal in a versioned envelope leaves room for
    // schema migrations without rewriting every existing run-row.

    private sealed record ProposalEnvelope(ZammadKbProposal Proposal)
    {
        public int Version { get; init; } = 1;
    }
}
